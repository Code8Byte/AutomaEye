<?php
require __DIR__ . '/../src/bootstrap.php';

$redirect = sanitize_app_redirect($_GET['redirect'] ?? $_POST['redirect'] ?? null);
$error = null;

if ($_SERVER['REQUEST_METHOD'] === 'POST') {
    if (!verify_csrf()) {
        $error = 'Your session expired. Please try again.';
    } else {
        $email = trim((string) ($_POST['email'] ?? ''));
        $password = (string) ($_POST['password'] ?? '');

        if ($email === '' || $password === '') {
            $error = 'Enter your email and password.';
        } else {
            $user = Auth::attempt($email, $password);
            if (!$user) {
                $error = 'Those credentials don\'t match an account.';
            } else {
                Auth::login($user);
                if ($redirect) {
                    $token = Auth::issueAppToken($user);
                    $sep = str_contains($redirect, '?') ? '&' : '?';
                    redirect($redirect . $sep . 'token=' . urlencode($token));
                }
                redirect('/welcome.php');
            }
        }
    }
    set_old(['email' => $_POST['email'] ?? '']);
}

if (Auth::check() && $_SERVER['REQUEST_METHOD'] !== 'POST') {
    redirect('/welcome.php');
}

if (!$error) {
    $error = flash('error');
}

$pageTitle = 'Log in — AutomaEye';
require __DIR__ . '/../src/includes/header.php';
?>
<canvas id="story-bg"></canvas>
<main class="auth-shell">
  <div class="auth-card">
    <h1>Welcome back</h1>
    <p class="sub">Log in to connect your account with the AutomaEye app.</p>

    <?php if ($error): ?><div class="alert alert-error"><?= e($error) ?></div><?php endif; ?>
    <?php if ($msg = flash('success')): ?><div class="alert alert-success"><?= e($msg) ?></div><?php endif; ?>

    <div class="social-row">
      <a class="btn btn-social btn-block<?= GOOGLE_CLIENT_ID ? '' : ' is-disabled' ?>" href="/auth/google.php?<?= $redirect ? 'redirect=' . urlencode($redirect) : '' ?>">Continue with Google</a>
      <a class="btn btn-social btn-block<?= GITHUB_CLIENT_ID ? '' : ' is-disabled' ?>" href="/auth/github.php?<?= $redirect ? 'redirect=' . urlencode($redirect) : '' ?>">Continue with GitHub</a>
    </div>
    <div class="divider">or</div>

    <form method="post" novalidate>
      <?= csrf_field() ?>
      <?php if ($redirect): ?><input type="hidden" name="redirect" value="<?= e($redirect) ?>"><?php endif; ?>
      <div class="field">
        <label for="email">Email</label>
        <input type="email" id="email" name="email" value="<?= old('email') ?>" required autofocus>
      </div>
      <div class="field">
        <div class="field-row">
          <label for="password">Password</label>
          <a class="link-muted" href="/forgot-password.php">Forgot password?</a>
        </div>
        <input type="password" id="password" name="password" required>
      </div>
      <button type="submit" class="btn btn-primary btn-block btn-lg">Log in</button>
    </form>

    <p class="foot-note">Don't have an account? <a href="/signup.php<?= $redirect ? '?redirect=' . urlencode($redirect) : '' ?>">Sign up</a></p>
  </div>
</main>
<?php clear_old(); require __DIR__ . '/../src/includes/footer.php'; ?>
