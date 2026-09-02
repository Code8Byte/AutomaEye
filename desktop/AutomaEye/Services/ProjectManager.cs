using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using AutomaEye.Models;

namespace AutomaEye.Services;

public class ProjectManager
{
    public const string ProjectFileName = "project.json";
    public const string ModelsDir = "models";
    public const string OutputsDir = "outputs";
    public const string DatasetDir = "dataset";
    public const string WeightsDir = "weights";

    /// <summary>
    /// Marker file (like .git) written into every project folder so a
    /// GitHub-connected repo's subfolders can be told apart from unrelated
    /// content - only folders carrying this are treated as AutomaEye projects.
    /// </summary>
    public const string MarkerFileName = ".automaeyes";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public string Root { get; }

    public ProjectManager(string root)
    {
        Root = root;
        Directory.CreateDirectory(root);
    }

    public List<Project> List()
    {
        var result = new List<Project>();
        foreach (var dir in Directory.GetDirectories(Root))
        {
            // Skip git/LFS internals and anything else that isn't ours - once
            // this folder is a GitHub-connected working tree it can contain
            // more than just project subfolders.
            if (Path.GetFileName(dir).StartsWith('.')) continue;
            try
            {
                result.Add(Load(Path.GetFileName(dir)));
            }
            catch
            {
                // skip corrupt / non-project directories
            }
        }
        result.Sort((a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));
        return result;
    }

    public Project Create(string name, string description)
    {
        name = Sanitize(name);
        if (string.IsNullOrEmpty(name)) throw new ArgumentException("Project name is empty");

        var dir = Path.Combine(Root, name);
        if (Directory.Exists(dir)) throw new InvalidOperationException($"Project \"{name}\" already exists");

        Directory.CreateDirectory(Path.Combine(dir, ModelsDir));
        Directory.CreateDirectory(Path.Combine(dir, OutputsDir));
        File.WriteAllText(Path.Combine(dir, MarkerFileName), $"{{\"app\":\"automaeyes\",\"version\":1,\"project\":\"{name}\"}}");

        var now = DateTime.UtcNow;
        var project = new Project
        {
            Name = name,
            Description = description,
            CreatedAt = now,
            UpdatedAt = now,
            Dir = dir,
        };
        Save(project);
        return project;
    }

    public Project Load(string name)
    {
        var dir = Path.Combine(Root, name);
        var path = Path.Combine(dir, ProjectFileName);
        var json = File.ReadAllText(path);
        var project = JsonSerializer.Deserialize<Project>(json, JsonOpts)
                      ?? throw new InvalidDataException($"Could not parse {path}");
        project.Dir = dir;

        foreach (var model in project.Models)
        {
            model.Dir = Path.Combine(dir, ModelsDir, model.Name);
        }

        // Backfill the marker for projects created before it existed, so a
        // push to GitHub always carries it.
        var markerPath = Path.Combine(dir, MarkerFileName);
        if (!File.Exists(markerPath))
        {
            File.WriteAllText(markerPath, $"{{\"app\":\"automaeyes\",\"version\":1,\"project\":\"{project.Name}\"}}");
        }

        return project;
    }

    public void Save(Project project)
    {
        project.UpdatedAt = DateTime.UtcNow;
        if (string.IsNullOrEmpty(project.Dir)) project.Dir = Path.Combine(Root, project.Name);
        Directory.CreateDirectory(project.Dir);
        var json = JsonSerializer.Serialize(project, JsonOpts);
        File.WriteAllText(Path.Combine(project.Dir, ProjectFileName), json);
    }

    public void Delete(string name)
    {
        var dir = Path.Combine(Root, name);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    public Model AddModel(Project project, string name, AIType type, List<Addon> addons, List<string> classes)
    {
        name = Sanitize(name);
        if (string.IsNullOrEmpty(name)) throw new ArgumentException("Model name is empty");
        if (project.FindModel(name) != null) throw new InvalidOperationException($"Model \"{name}\" already exists");

        var modelDir = Path.Combine(project.Dir, ModelsDir, name);
        foreach (var sub in new[] { "dataset/images/train", "dataset/images/val", "dataset/labels/train", "dataset/labels/val", WeightsDir, "runs" })
        {
            Directory.CreateDirectory(Path.Combine(modelDir, sub.Replace('/', Path.DirectorySeparatorChar)));
        }
        WriteDataYaml(modelDir, classes);

        var now = DateTime.UtcNow;
        var model = new Model
        {
            Name = name,
            Type = type,
            Addons = addons,
            Classes = classes,
            CreatedAt = now,
            UpdatedAt = now,
            Dir = modelDir,
        };
        project.Models.Add(model);
        Save(project);
        return model;
    }

    public void DeleteModel(Project project, string name)
    {
        var model = project.FindModel(name) ?? throw new InvalidOperationException($"Model \"{name}\" not found");
        if (Directory.Exists(model.Dir)) Directory.Delete(model.Dir, recursive: true);
        project.Models.Remove(model);
        Save(project);
    }

    public void SetWorkflow(Project project, List<WorkflowStep> steps, string onFirstNG)
    {
        project.Workflow.Steps = steps;
        project.Workflow.OnFirstNG = onFirstNG;
        Save(project);
    }

    public void SetOutput(Project project, string mode, string script)
    {
        if (mode != "signal" && mode != "script") throw new ArgumentException($"Unknown output mode \"{mode}\"");
        project.Output.Mode = mode;
        project.Output.Script = script;
        Save(project);
    }

    private static void WriteDataYaml(string modelDir, List<string> classes)
    {
        var names = string.Join(", ", classes.Select(c => $"'{c}'"));
        var yaml = $"""
            path: {modelDir.Replace('\\', '/')}/dataset
            train: images/train
            val: images/val
            names: [{names}]
            """;
        File.WriteAllText(Path.Combine(modelDir, DatasetDir, "data.yaml"), yaml);
    }

    private static string Sanitize(string s)
    {
        s = s.Trim();
        s = Regex.Replace(s, @"[^\w\-. ]", "");
        return s;
    }
}
