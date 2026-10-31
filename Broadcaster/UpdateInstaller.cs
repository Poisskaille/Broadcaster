using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Broadcaster
{
    /// <summary>
    /// Télécharge et prépare la release en dehors du dossier d'installation.
    /// Un script PowerShell séparé prend le relais APRÈS la fermeture de Broadcaster
    /// pour pouvoir remplacer Broadcaster.exe et ses DLL.
    /// </summary>
    public static class UpdateInstaller
    {
        private const long MaxZipBytes = 512L * 1024 * 1024;
        private const long MaxExtractedBytes = 1024L * 1024 * 1024;
        private const int MaxZipEntries = 5000;

        private static readonly HttpClient DownloadClient = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(5)
        };

        static UpdateInstaller()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            DownloadClient.DefaultRequestHeaders.UserAgent.ParseAdd("Broadcaster-Updater");
        }

        public static async Task PrepareAndLaunchAsync(UpdateInfo release, IProgress<int> progress)
        {
            if (release == null || !release.IsNewer || string.IsNullOrWhiteSpace(release.DownloadUrl))
                throw new InvalidOperationException("Aucune mise à jour téléchargeable n'a été sélectionnée.");

            // Ne jamais accepter un lien arbitraire provenant d'une réponse API inattendue.
            Uri downloadUri;
            if (!Uri.TryCreate(release.DownloadUrl, UriKind.Absolute, out downloadUri) ||
                downloadUri.Scheme != Uri.UriSchemeHttps ||
                !downloadUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
                !downloadUri.AbsolutePath.StartsWith("/Poisskaille/Broadcaster/releases/download/", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("L'adresse de téléchargement GitHub n'est pas reconnue.");
            }

            string exePath = Application.ExecutablePath;
            string exeName = Path.GetFileName(exePath);
            string installDir = Path.GetDirectoryName(exePath);
            if (string.IsNullOrWhiteSpace(installDir) || string.IsNullOrWhiteSpace(exeName))
                throw new InvalidOperationException("Impossible de retrouver le dossier de l'application.");

            string workDir = Path.Combine(Path.GetTempPath(), "Broadcaster-Update-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDir);
            bool launched = false;
            try
            {
                string zipPath = Path.Combine(workDir, "release.zip");
                await DownloadZipAsync(downloadUri, zipPath, progress).ConfigureAwait(true);

                if (!string.IsNullOrWhiteSpace(release.Sha256))
                    await Task.Run(() => VerifySha256(zipPath, release.Sha256)).ConfigureAwait(true);

                string extractDir = Path.Combine(workDir, "extracted");
                string payloadDir = await Task.Run(() => ExtractAndVerify(zipPath, extractDir, exeName, release.LatestVersion))
                    .ConfigureAwait(true);

                // Pas de téléchargement ou d'extraction une fois l'appli fermée.
                string scriptPath = Path.Combine(workDir, "apply-update.ps1");
                File.WriteAllText(scriptPath, ApplyUpdatePowerShell, new UTF8Encoding(true));
                File.WriteAllLines(Path.Combine(workDir, "manifest.txt"), new[]
                {
                    Process.GetCurrentProcess().Id.ToString(),
                    installDir,
                    payloadDir,
                    exeName
                }, new UTF8Encoding(true));

                // powershell.exe est un composant Windows présent avec .NET Framework 4.8.
                // Le fichier de commande et le ZIP sont sous un dossier temporaire unique.
                var psi = new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                        @"WindowsPowerShell\v1.0\powershell.exe"),
                    Arguments = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" +
                        scriptPath.Replace("\"", "") + "\"",
                    WorkingDirectory = workDir,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (Process updater = Process.Start(psi))
                {
                    if (updater == null)
                        throw new InvalidOperationException("Le programme de mise à jour n'a pas pu démarrer.");
                }
                launched = true;
            }
            finally
            {
                // En cas d'échec on ne laisse ni téléchargement ni script orphelin.
                // Après lancement, le script utilise ce dossier : il le nettoiera lui-même.
                if (!launched)
                {
                    try { Directory.Delete(workDir, true); } catch { }
                }
            }
        }

        private static async Task DownloadZipAsync(Uri uri, string destination, IProgress<int> progress)
        {
            using (var response = await DownloadClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                long? expected = response.Content.Headers.ContentLength;
                if (expected.HasValue && expected.Value > MaxZipBytes)
                    throw new InvalidOperationException("La release ZIP dépasse la limite autorisée de 512 Mo.");

                using (Stream input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[81920];
                    long received = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                    {
                        received += read;
                        if (received > MaxZipBytes)
                            throw new InvalidOperationException("La release ZIP dépasse la limite autorisée de 512 Mo.");
                        await output.WriteAsync(buffer, 0, read).ConfigureAwait(false);
                        if (expected.GetValueOrDefault() > 0)
                            progress?.Report((int)Math.Min(100, received * 100 / expected.Value));
                    }
                    if (expected.HasValue && received != expected.Value)
                        throw new InvalidDataException("Le téléchargement du ZIP est incomplet.");
                }
            }
            progress?.Report(100);
        }

        private static void VerifySha256(string filename, string expectedDigest)
        {
            if (!expectedDigest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ||
                expectedDigest.Length != 71)
                throw new InvalidDataException("L'empreinte fournie par GitHub est invalide.");

            using (var sha = SHA256.Create())
            using (var file = File.OpenRead(filename))
            {
                var actual = BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "");
                if (!actual.Equals(expectedDigest.Substring(7), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("L'empreinte SHA-256 du ZIP ne correspond pas à celle de GitHub.");
            }
        }

        private static string ExtractAndVerify(string zipFile, string destination, string exeName, Version expectedVersion)
        {
            Directory.CreateDirectory(destination);
            string destRoot = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            long totalBytes = 0;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            using (var archive = new ZipArchive(File.OpenRead(zipFile), ZipArchiveMode.Read))
            {
                if (archive.Entries.Count == 0 || archive.Entries.Count > MaxZipEntries)
                    throw new InvalidDataException("Le ZIP contient un nombre anormal de fichiers.");

                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string relative = entry.FullName.Replace('\\', '/');
                    string[] segments = relative.Split('/');
                    if (relative.StartsWith("/", StringComparison.Ordinal) ||
                        relative.IndexOf(':') >= 0 ||
                        Array.Exists(segments, s => s == ".." || s == "."))
                        throw new InvalidDataException("Chemin interdit dans le ZIP : " + entry.FullName);

                    // Rejette les liens symboliques Unix : ils pourraient sortir du dossier temporaire.
                    if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                        throw new InvalidDataException("Le ZIP contient un lien symbolique non autorisé.");

                    string destPath = Path.GetFullPath(Path.Combine(destination,
                        relative.Replace('/', Path.DirectorySeparatorChar)));
                    if (!destPath.StartsWith(destRoot, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Un chemin ZIP tente de sortir du dossier d'extraction.");
                    if (!seen.Add(destPath))
                        throw new InvalidDataException("Entrée ZIP en double : " + entry.FullName);

                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        Directory.CreateDirectory(destPath);
                        continue;
                    }

                    totalBytes += entry.Length;
                    if (totalBytes > MaxExtractedBytes)
                        throw new InvalidDataException("La taille décompressée dépasse 1 Go.");

                    Directory.CreateDirectory(Path.GetDirectoryName(destPath));
                    using (Stream source = entry.Open())
                    using (var target = new FileStream(destPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        source.CopyTo(target);
                }
            }

            // Les ZIP peuvent contenir un dossier englobant (par ex. Broadcaster-v1.5/).
            // On installe les fichiers à partir du répertoire contenant l'unique exécutable.
            string[] executables = Directory.GetFiles(destination, exeName, SearchOption.AllDirectories);
            if (executables.Length != 1)
                throw new InvalidDataException("Le ZIP doit contenir exactement un fichier " + exeName + ".");

            try
            {
                Version packagedVersion = AssemblyName.GetAssemblyName(executables[0]).Version;
                if (expectedVersion == null || !Normalize(packagedVersion).Equals(Normalize(expectedVersion)))
                    throw new InvalidDataException("La version de l'exécutable dans le ZIP ne correspond pas à la release GitHub.");
            }
            catch (BadImageFormatException ex)
            {
                throw new InvalidDataException("La release ne contient pas un exécutable .NET Broadcaster valide.", ex);
            }
            return Path.GetDirectoryName(executables[0]);
        }

        private static Version Normalize(Version v) => new Version(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

        private const string ApplyUpdatePowerShell = @"
$ErrorActionPreference = 'Stop'
$baseDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$manifest = [System.IO.File]::ReadAllLines((Join-Path $baseDir 'manifest.txt'), [System.Text.Encoding]::UTF8)
$parentProcessId = [int]$manifest[0]
$installDir = $manifest[1]
$payloadDir = $manifest[2]
$exeName = $manifest[3]
$exePath = Join-Path $installDir $exeName
$backupDir = Join-Path $baseDir 'backup'
$created = New-Object 'System.Collections.Generic.List[string]'
$backedUp = New-Object 'System.Collections.Generic.List[string]'
$startedReplacing = $false

try {
    $deadline = (Get-Date).AddSeconds(120)
    while (Get-Process -Id $parentProcessId -ErrorAction SilentlyContinue) {
        if ((Get-Date) -gt $deadline) { throw 'Broadcaster ne se ferme pas. La mise à jour est annulée.' }
        Start-Sleep -Milliseconds 500
    }

    if (-not (Test-Path -LiteralPath (Join-Path $payloadDir $exeName) -PathType Leaf)) {
        throw 'Le ZIP ne contient pas Broadcaster.exe à la racine du contenu à installer.'
    }

    $files = @(Get-ChildItem -LiteralPath $payloadDir -File -Recurse)
    if ($files.Count -eq 0) { throw 'Aucun fichier à installer.' }
    $startedReplacing = $true

    foreach ($file in $files) {
        $relative = $file.FullName.Substring($payloadDir.Length).TrimStart('\')
        # Les configurations et contenus personnels ne sont jamais écrasés.
        if ($relative -ieq 'config.ini' -or
            $relative -like 'Screenshots\*' -or
            $relative -like 'ColorProfiles\*') { continue }

        $target = Join-Path $installDir $relative
        $targetFolder = Split-Path -Parent $target

        # Vérifier les chemins avant de créer des répertoires (jonctions et liens).
        $current = $targetFolder
        while ($current.Length -ge $installDir.Length) {
            if ((Test-Path -LiteralPath $current) -and
                ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw ('Lien de dossier non autorisé : ' + $current)
            }
            if ($current -ieq $installDir) { break }
            $next = Split-Path -Parent $current
            if ($next -eq $current -or [string]::IsNullOrEmpty($next)) { break }
            $current = $next
        }
        if ((Test-Path -LiteralPath $target) -and
            ((Get-Item -LiteralPath $target -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw ('Lien de fichier non autorisé : ' + $target)
        }
        if (-not (Test-Path -LiteralPath $targetFolder -PathType Container)) {
            New-Item -ItemType Directory -Path $targetFolder -Force | Out-Null
        }

        if (Test-Path -LiteralPath $target -PathType Leaf) {
            $copyBackup = Join-Path $backupDir $relative
            $backupFolder = Split-Path -Parent $copyBackup
            if (-not (Test-Path -LiteralPath $backupFolder)) {
                New-Item -ItemType Directory -Path $backupFolder -Force | Out-Null
            }
            Copy-Item -LiteralPath $target -Destination $copyBackup -Force
            $backedUp.Add($relative)
        } else {
            $created.Add($target)
        }

        Copy-Item -LiteralPath $file.FullName -Destination $target -Force
    }

    Start-Process -FilePath $exePath -WorkingDirectory $installDir
    # Le répertoire temporaire comprend maintenant uniquement des fichiers jetables.
    try { Remove-Item -LiteralPath $baseDir -Recurse -Force -ErrorAction Stop } catch { }
} catch {
    $reason = $_.Exception.Message
    if ($startedReplacing) {
        # Retour arrière : restaurer les fichiers remplacés et supprimer les nouveaux.
        foreach ($relative in $backedUp) {
            try {
                Copy-Item -LiteralPath (Join-Path $backupDir $relative) -Destination (Join-Path $installDir $relative) -Force
            } catch { $reason += ' | Echec de restauration : ' + $relative }
        }
        foreach ($path in $created) {
            try { Remove-Item -LiteralPath $path -Force -ErrorAction Stop } catch { }
        }
    }
    try {
        $logDir = Join-Path $env:LOCALAPPDATA 'Broadcaster\Updater'
        New-Item -ItemType Directory -Path $logDir -Force | Out-Null
        $reason | Out-File -LiteralPath (Join-Path $logDir 'last-error.log') -Encoding UTF8
    } catch { }
    try { if (Test-Path -LiteralPath $exePath) { Start-Process -FilePath $exePath -WorkingDirectory $installDir } } catch { }
    try {
        Add-Type -AssemblyName System.Windows.Forms
        [System.Windows.Forms.MessageBox]::Show(
            ('La mise à jour de Broadcaster a échoué. Les anciens fichiers ont été restaurés si possible.' + [Environment]::NewLine + $reason),
            'Broadcaster - mise à jour', 'OK', 'Error') | Out-Null
    } catch { }
    exit 1
}
";
    }
}
