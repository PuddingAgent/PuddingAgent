using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text.Json;
using PuddingSlack;

namespace PuddingAgent.Connectors;

/// <summary>Secret file owner. Temporary files start with the existing ACL, before any credential is written.</summary>
public sealed class SlackCredentialFile : ISlackCredentialStore, IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;
    private readonly FileStream _lease;
    private readonly object _gate = new();

    public SlackCredentialFile(string path)
    {
        _path = Path.GetFullPath(path);
        _lease = new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    public SlackCredentialSnapshot Read()
    {
        lock (_gate)
        {
            var bytes = File.ReadAllBytes(_path);
            var credentials = JsonSerializer.Deserialize<SlackCredentials>(bytes, Json)?.Validate()
                ?? throw new SlackProtocolException("credential_file_invalid");
            return new(credentials, Convert.ToHexString(SHA256.HashData(bytes)));
        }
    }

    public SlackCredentialSnapshot Write(SlackCredentials credentials, string expectedRevision)
    {
        lock (_gate)
        {
            if (Read().Revision != expectedRevision) throw new IOException("Slack credential file changed during refresh.");
            var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = CreateSecretFile(temporary))
                {
                    JsonSerializer.Serialize(stream, credentials.Validate(), Json);
                    stream.Flush(true);
                }
                if (Read().Revision != expectedRevision) throw new IOException("Slack credential file changed during refresh.");
                File.Replace(temporary, _path, null);
                return Read();
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    private FileStream CreateSecretFile(string temporary)
    {
        if (OperatingSystem.IsWindows())
        {
            var security = new FileInfo(_path).GetAccessControl(AccessControlSections.Access);
            return new FileInfo(temporary).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None,
                4096, FileOptions.WriteThrough, security);
        }
        return new FileStream(temporary, new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
            Options = FileOptions.WriteThrough, UnixCreateMode = File.GetUnixFileMode(_path)
        });
    }

    public void Dispose() => _lease.Dispose();
}
