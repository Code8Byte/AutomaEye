<?php
declare(strict_types=1);

final class Auth
{
    public static function findByEmail(string $email): ?array
    {
        $stmt = Database::connection()->prepare('SELECT * FROM users WHERE email = :email');
        $stmt->execute(['email' => strtolower(trim($email))]);
        $user = $stmt->fetch();
        return $user ?: null;
    }

    public static function findById(int $id): ?array
    {
        $stmt = Database::connection()->prepare('SELECT * FROM users WHERE id = :id');
        $stmt->execute(['id' => $id]);
        $user = $stmt->fetch();
        return $user ?: null;
    }

    public static function register(string $name, string $email, string $password): array
    {
        $pdo = Database::connection();
        $stmt = $pdo->prepare(
            'INSERT INTO users (name, email, password_hash, provider) VALUES (:name, :email, :hash, :provider)'
        );
        $stmt->execute([
            'name' => trim($name),
            'email' => strtolower(trim($email)),
            'hash' => password_hash($password, PASSWORD_DEFAULT),
            'provider' => 'local',
        ]);

        return self::findById((int) $pdo->lastInsertId());
    }

    public static function findOrCreateFromProvider(string $provider, string $providerId, string $name, string $email, ?string $avatarUrl = null): array
    {
        $existing = self::findByEmail($email);
        if ($existing) {
            return $existing;
        }

        $pdo = Database::connection();
        $stmt = $pdo->prepare(
            'INSERT INTO users (name, email, provider, provider_id, avatar_url) VALUES (:name, :email, :provider, :provider_id, :avatar_url)'
        );
        $stmt->execute([
            'name' => $name,
            'email' => strtolower(trim($email)),
            'provider' => $provider,
            'provider_id' => $providerId,
            'avatar_url' => $avatarUrl,
        ]);

        return self::findById((int) $pdo->lastInsertId());
    }

    public static function attempt(string $email, string $password): ?array
    {
        $user = self::findByEmail($email);
        if (!$user || !$user['password_hash'] || !password_verify($password, $user['password_hash'])) {
            return null;
        }
        return $user;
    }

    public static function login(array $user): void
    {
        session_regenerate_id(true);
        $_SESSION['user_id'] = $user['id'];
    }

    public static function logout(): void
    {
        $_SESSION = [];
        session_regenerate_id(true);
    }

    public static function user(): ?array
    {
        if (empty($_SESSION['user_id'])) {
            return null;
        }
        return self::findById((int) $_SESSION['user_id']);
    }

    public static function check(): bool
    {
        return !empty($_SESSION['user_id']);
    }

    public static function issueAppToken(array $user): string
    {
        // Short-lived signed token the desktop app exchanges for a session.
        // HMAC over user id + expiry, keyed by a server secret derived from the session.
        $secret = self::appSecret();
        $expires = time() + 300;
        $payload = $user['id'] . '.' . $expires;
        $signature = hash_hmac('sha256', $payload, $secret);
        return base64_encode($payload . '.' . $signature);
    }

    private static function appSecret(): string
    {
        $keyFile = STORAGE_DIR . '/app_secret.key';
        if (!file_exists($keyFile)) {
            file_put_contents($keyFile, bin2hex(random_bytes(32)));
            chmod($keyFile, 0600);
        }
        return trim((string) file_get_contents($keyFile));
    }
}
