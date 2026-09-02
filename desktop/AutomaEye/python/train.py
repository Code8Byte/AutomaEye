"""
Per-model training script, invoked by AutomaEye's C# TrainingService via
Process.Start (adapted from the reference Electron app's python/train.py -
this is the same Ultralytics call, just launched from a .NET host instead of
Node's child_process, per the project's own "keep Python for what Python is
good at, C# just drives the process" design).

Progress format parsed by TrainingService:
  "PROGRESS_EPOCH <e>/<total>"     -> epoch progress bar
  "EPOCH_METRICS {json}"           -> per-epoch metrics for the training dashboard
  "results mAP50: .. P: .. R: .."  -> final metrics line
  "Saved: <path>"                  -> best.pt written
  "Exported: <path>"               -> best.onnx written (C# inference reads this one)
All errors print a clear message then exit 1 so the UI can show them.
"""
import argparse
import shutil
import sys
import traceback
from pathlib import Path

BASE_BY_TYPE = {
    "AI Segmentation": "yolo11n-seg.pt",
    "AI Detection": "yolo11n.pt",
    "AI Classification": "yolo11n-cls.pt",
    "AI OCR": "yolo11n.pt",
}


def count_labeled(images_dir, labels_dir):
    if not images_dir.exists():
        return 0, 0
    imgs = [p for p in images_dir.iterdir()
            if p.suffix.lower() in (".jpg", ".jpeg", ".png")]
    labeled = sum(1 for p in imgs if (labels_dir / (p.stem + ".txt")).exists())
    return len(imgs), labeled


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--project", required=True)
    ap.add_argument("--project-dir", required=True)
    ap.add_argument("--model", required=True)
    ap.add_argument("--model-dir", required=True)
    ap.add_argument("--data", required=True)
    ap.add_argument("--epochs", type=int, default=100)
    ap.add_argument("--batch", type=int, default=16)
    ap.add_argument("--imgsz", type=int, default=640)
    ap.add_argument("--lr", type=float, default=0.01)
    ap.add_argument("--type", required=True)
    ap.add_argument("--resume", action="store_true",
                     help="Continue training from runs/train/weights/last.pt")
    args = ap.parse_args()

    ds = Path(args.data).parent

    # Self-heal the "path:" line in data.yaml: it's an absolute path, so
    # after a git-synced project moves to a different PC/folder it points at
    # the old machine's dataset location and training fails. Rewrite it to
    # where the dataset actually is right now.
    try:
        data_file = Path(args.data)
        if data_file.exists():
            correct_path = str(ds.resolve()).replace("\\", "/")
            lines = data_file.read_text(encoding="utf-8").splitlines()
            new_lines, patched = [], False
            for ln in lines:
                if ln.strip().lower().startswith("path:"):
                    new_lines.append(f"path: {correct_path}")
                    patched = True
                else:
                    new_lines.append(ln)
            if not patched:
                new_lines.insert(0, f"path: {correct_path}")
            data_file.write_text("\n".join(new_lines) + "\n", encoding="utf-8")
            print(f"data.yaml path -> {correct_path}", flush=True)
    except Exception as e:
        print(f"[!] Failed to auto-fix data.yaml path: {e}", flush=True)

    n_img, n_lbl = count_labeled(ds / "images" / "train", ds / "labels" / "train")
    n_val, n_val_lbl = count_labeled(ds / "images" / "val", ds / "labels" / "val")
    print(f"Dataset: train {n_img} images ({n_lbl} labeled), val {n_val} images", flush=True)

    if n_img == 0:
        print("[X] No training images. Import/split the dataset first.", flush=True)
        sys.exit(1)
    if n_lbl == 0:
        print("[X] No labels MATCH the training images.", flush=True)
        print("    Label filenames must match image filenames.", flush=True)
        sys.exit(1)

    try:
        from ultralytics import YOLO
    except ImportError:
        print("[X] ultralytics is not installed. Run: pip install ultralytics", flush=True)
        sys.exit(1)

    # Keep the PC responsive during training, especially on CPU:
    # 1) lower this process's priority so the UI still gets CPU time.
    # 2) cap PyTorch's thread count, leaving 1-2 cores for the system.
    try:
        import ctypes
        ctypes.windll.kernel32.SetPriorityClass(
            ctypes.windll.kernel32.GetCurrentProcess(), 0x00004000)  # BELOW_NORMAL
        print("Training process priority: BelowNormal (UI stays responsive).", flush=True)
    except Exception:
        pass
    try:
        import os as _os
        import torch as _torch
        total = _os.cpu_count() or 4
        if _torch.cuda.is_available():
            print(f"Device: GPU {_torch.cuda.get_device_name(0)}", flush=True)
        else:
            keep = 2 if total > 4 else 1
            use = max(1, total - keep)
            _torch.set_num_threads(use)
            print(f"Device: CPU - using {use}/{total} threads (leaving {keep} for the UI). "
                  "CPU training is slow; install CUDA PyTorch if you have an NVIDIA GPU.",
                  flush=True)
    except Exception as _e:
        print(f"[!] Failed to set thread/priority: {_e}", flush=True)

    model_dir = Path(args.model_dir)
    weights_dir = model_dir / "weights"
    runs_dir = model_dir / "runs"
    weights_dir.mkdir(parents=True, exist_ok=True)

    last_ckpt = runs_dir / "train" / "weights" / "last.pt"
    do_resume = bool(args.resume) and last_ckpt.exists()
    if do_resume:
        base = str(last_ckpt)
        print(f"Resume: continuing training from {last_ckpt}", flush=True)
    else:
        existing = weights_dir / "best.pt"
        base = str(existing) if existing.exists() else BASE_BY_TYPE.get(args.type, "yolo11n.pt")
        print(f"Base: {base}", flush=True)

    try:
        model = YOLO(base)

        def on_epoch_end(trainer):
            try:
                e = int(getattr(trainer, "epoch", 0)) + 1
                print(f"PROGRESS_EPOCH {e}/{args.epochs}", flush=True)
            except Exception:
                pass
        model.add_callback("on_train_epoch_end", on_epoch_end)

        def on_fit_epoch_end(trainer):
            try:
                import json as _json
                e = int(getattr(trainer, "epoch", 0)) + 1
                mt = getattr(trainer, "metrics", None) or {}

                def _g(*keys):
                    for k in keys:
                        if k in mt:
                            try:
                                return float(mt[k])
                            except Exception:
                                pass
                    return 0.0

                prec = _g("metrics/precision(B)")
                rec = _g("metrics/recall(B)")
                map50 = _g("metrics/mAP50(B)")
                map5095 = _g("metrics/mAP50-95(B)")

                box = cls = dfl = 0.0
                try:
                    li = trainer.label_loss_items(trainer.tloss, prefix="train")
                    box = float(li.get("train/box_loss", 0.0))
                    cls = float(li.get("train/cls_loss", 0.0))
                    dfl = float(li.get("train/dfl_loss", 0.0))
                except Exception:
                    box = _g("val/box_loss")
                    cls = _g("val/cls_loss")
                    dfl = _g("val/dfl_loss")

                valBox = _g("val/box_loss")
                valCls = _g("val/cls_loss")
                valDfl = _g("val/dfl_loss")

                f1 = (2 * prec * rec / (prec + rec)) if (prec + rec) > 0 else 0.0
                print("EPOCH_METRICS " + _json.dumps({
                    "epoch": e, "total": args.epochs,
                    "precision": prec, "recall": rec,
                    "mAP50": map50, "mAP5095": map5095,
                    "boxLoss": box, "clsLoss": cls, "dflLoss": dfl, "f1": f1,
                    "valBox": valBox, "valCls": valCls, "valDfl": valDfl,
                }), flush=True)
            except Exception:
                pass
        model.add_callback("on_fit_epoch_end", on_fit_epoch_end)

        if do_resume:
            results = model.train(resume=True)
        else:
            results = model.train(
                data=args.data,
                epochs=args.epochs,
                batch=args.batch,
                imgsz=args.imgsz,
                lr0=args.lr,
                project=str(runs_dir),
                name="train",
                exist_ok=True,
                verbose=True,
            )
    except Exception as e:
        print(f"[X] Training failed: {e}", flush=True)
        traceback.print_exc()
        sys.exit(1)

    best = Path(results.save_dir) / "weights" / "best.pt"
    if best.exists():
        target = weights_dir / "best.pt"
        shutil.copy2(best, target)
        print(f"Saved: {target}", flush=True)

        # C# inference runs on ONNX Runtime, not raw PyTorch weights - export
        # right after training so the trained model is immediately usable in
        # a workflow without a separate manual export step.
        try:
            best_model = YOLO(str(target))
            onnx_path = best_model.export(format="onnx", imgsz=args.imgsz)
            onnx_target = weights_dir / "best.onnx"
            if str(onnx_path) != str(onnx_target):
                shutil.copy2(onnx_path, onnx_target)
            print(f"Exported: {onnx_target}", flush=True)
        except Exception as e:
            print(f"[!] ONNX export failed (best.pt is still saved): {e}", flush=True)

    m = results.results_dict if hasattr(results, "results_dict") else {}
    mAP = m.get("metrics/mAP50(B)", 0.0)
    mAP5095 = m.get("metrics/mAP50-95(B)", 0.0)
    P = m.get("metrics/precision(B)", 0.0)
    R = m.get("metrics/recall(B)", 0.0)
    print(f"results mAP50: {mAP:.4f} mAP50-95: {mAP5095:.4f} P: {P:.4f} R: {R:.4f}", flush=True)


if __name__ == "__main__":
    main()
