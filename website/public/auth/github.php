<?php
require __DIR__ . '/../../src/bootstrap.php';

if (!GITHUB_CLIENT_ID) {
    flash('error', 'GitHub sign-in is not configured yet.');
    redirect('/login.php');
}

$redirect = sanitize_app_redirect($_GET['redirect'] ?? null);
$state = OAuth::state('github', $redirect);

$params = http_build_query([
    'client_id' => GITHUB_CLIENT_ID,
    'redirect_uri' => APP_URL . '/auth/callback.php?provider=github',
    'scope' => 'read:user user:email',
    'state' => $state,
]);

redirect('https://github.com/login/oauth/authorize?' . $params);
