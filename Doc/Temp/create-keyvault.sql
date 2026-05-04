CREATE TABLE IF NOT EXISTS KeyVaults (
    Id INTEGER NOT NULL CONSTRAINT PK_KeyVaults PRIMARY KEY AUTOINCREMENT,
    KeyVaultId TEXT NOT NULL,
    Name TEXT NOT NULL,
    Description TEXT,
    EncryptedValue TEXT NOT NULL,
    Category TEXT NOT NULL DEFAULT 'general',
    Tags TEXT,
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS IX_KeyVaults_KeyVaultId ON KeyVaults(KeyVaultId);
CREATE UNIQUE INDEX IF NOT EXISTS IX_KeyVaults_Name ON KeyVaults(Name);
INSERT OR IGNORE INTO __EFMigrationsHistory(MigrationId, ProductVersion) VALUES('20260503123000_AddKeyVault', '10.0.0');
