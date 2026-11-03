// WorkList-Setup.exe - installs / uninstalls Work List for the current Windows user.
//
//   WorkList-Setup.exe                     show the install window
//   WorkList-Setup.exe /silent             install without asking (starts with Windows)
//   WorkList-Setup.exe /silent /nostartup  same, but don't start with Windows
//   Uninstall.exe /uninstall               uninstall (this is what Settings > Apps runs)
//   Uninstall.exe /uninstall /silent       uninstall without asking (keeps saved tasks)
//
// Installs to %LOCALAPPDATA%\Programs\WorkList - no admin rights needed.
// The app itself (WorkList.exe) is embedded inside this file at build time.

using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Work List Setup")]
[assembly: AssemblyProduct("Work List")]
[assembly: AssemblyDescription("Installs or removes Work List")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace WorkListSetup
{
    static class Paths
    {
#if TESTHOOKS
        // Test builds only: WORKLIST_TEST_ROOT redirects every location (files, shortcuts, registry)
        // into a sandbox so the real system is never touched. Not in the version you install.
        static readonly string TestRoot = Environment.GetEnvironmentVariable("WORKLIST_TEST_ROOT");
#else
        static readonly string TestRoot = null;
#endif
        public static bool Test { get { return !string.IsNullOrEmpty(TestRoot); } }

        static string Pick(string real, string testRelative) { return Test ? Path.Combine(TestRoot, testRelative) : real; }
        static string Folder(Environment.SpecialFolder f) { return Environment.GetFolderPath(f); }

        public static string InstallDir { get { return Pick(Path.Combine(Folder(Environment.SpecialFolder.LocalApplicationData), "Programs", "WorkList"), @"Programs\WorkList"); } }
        public static string AppExe { get { return Path.Combine(InstallDir, "WorkList.exe"); } }
        public static string Uninstaller { get { return Path.Combine(InstallDir, "Uninstall.exe"); } }
        public static string StartMenuLink { get { return Pick(Path.Combine(Folder(Environment.SpecialFolder.Programs), "Work List.lnk"), @"StartMenu\Work List.lnk"); } }
        public static string StartupLink { get { return Pick(Path.Combine(Folder(Environment.SpecialFolder.Startup), "Work List.lnk"), @"Startup\Work List.lnk"); } }
        public static string DesktopLink { get { return Pick(Path.Combine(Folder(Environment.SpecialFolder.DesktopDirectory), "Work List.lnk"), @"Desktop\Work List.lnk"); } }
        public static string DataDir { get { return Pick(Path.Combine(Folder(Environment.SpecialFolder.ApplicationData), "WorkList"), "Data"); } }
        public static string UninstallKey { get { return Test ? @"Software\WorkListTest\Uninstall\WorkList" : @"Software\Microsoft\Windows\CurrentVersion\Uninstall\WorkList"; } }
    }

    static class Installer
    {
        public const string Version = "1.0.0";

        public static bool IsInstalled
        {
            get { using (RegistryKey k = Registry.CurrentUser.OpenSubKey(Paths.UninstallKey)) return k != null; }
        }

        public static void Install(bool startWithWindows, bool desktopShortcut)
        {
            if (!Paths.Test) StopRunningApp();

            Directory.CreateDirectory(Paths.InstallDir);
            using (Stream src = Assembly.GetExecutingAssembly().GetManifestResourceStream("WorkList.exe"))
            {
                if (src == null) throw new InvalidOperationException("This installer is damaged (the app is missing inside it).");
                using (FileStream dst = File.Create(Paths.AppExe)) src.CopyTo(dst);
            }

            string self = Assembly.GetExecutingAssembly().Location;
            if (!SamePath(self, Paths.Uninstaller)) File.Copy(self, Paths.Uninstaller, true);

            CreateShortcut(Paths.StartMenuLink, Paths.AppExe);
            if (startWithWindows) CreateShortcut(Paths.StartupLink, Paths.AppExe); else TryDelete(Paths.StartupLink);
            if (desktopShortcut) CreateShortcut(Paths.DesktopLink, Paths.AppExe); else TryDelete(Paths.DesktopLink);

            long bytes = new FileInfo(Paths.AppExe).Length + new FileInfo(Paths.Uninstaller).Length;
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(Paths.UninstallKey))
            {
                k.SetValue("DisplayName", "Work List");
                k.SetValue("DisplayVersion", Version);
                k.SetValue("Publisher", "Work List");
                k.SetValue("DisplayIcon", Paths.AppExe);
                k.SetValue("InstallLocation", Paths.InstallDir);
                k.SetValue("InstallDate", DateTime.Today.ToString("yyyyMMdd"));
                k.SetValue("UninstallString", "\"" + Paths.Uninstaller + "\" /uninstall");
                k.SetValue("QuietUninstallString", "\"" + Paths.Uninstaller + "\" /uninstall /silent");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                k.SetValue("EstimatedSize", (int)(bytes / 1024), RegistryValueKind.DWord);
            }
        }

        public static void Uninstall(bool deleteSavedTasks)
        {
            if (!Paths.Test) StopRunningApp();

            TryDelete(Paths.StartMenuLink);
            TryDelete(Paths.StartupLink);
            TryDelete(Paths.DesktopLink);
            Registry.CurrentUser.DeleteSubKeyTree(Paths.UninstallKey, false);
            TryDelete(Paths.AppExe);
            if (deleteSavedTasks) TryDeleteDir(Paths.DataDir);

            // A running program can't delete itself, so when Uninstall.exe runs from the
            // install folder, a hidden command removes the folder a moment after we exit.
            string self = Assembly.GetExecutingAssembly().Location;
            if (SamePath(Path.GetDirectoryName(self), Paths.InstallDir))
            {
                var psi = new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"" + Paths.InstallDir + "\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetTempPath(),
                };
                Process.Start(psi);
            }
            else TryDeleteDir(Paths.InstallDir);
        }

        public static void LaunchApp()
        {
            Process.Start(new ProcessStartInfo(Paths.AppExe) { WorkingDirectory = Paths.InstallDir, UseShellExecute = true });
        }

        static void StopRunningApp()
        {
            foreach (Process p in Process.GetProcessesByName("WorkList"))
            {
                try { p.Kill(); p.WaitForExit(3000); } catch { }
            }
        }

        static void CreateShortcut(string linkPath, string target)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(linkPath));
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(shellType);
            try
            {
                object link = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { linkPath });
                Type lt = link.GetType();
                lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, new object[] { target });
                lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, link, new object[] { Path.GetDirectoryName(target) });
                lt.InvokeMember("Description", BindingFlags.SetProperty, null, link, new object[] { "Work List - your to-do list, one click away" });
                lt.InvokeMember("IconLocation", BindingFlags.SetProperty, null, link, new object[] { target + ",0" });
                lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
                Marshal.FinalReleaseComObject(link);
            }
            finally { Marshal.FinalReleaseComObject(shell); }
        }

        static bool SamePath(string a, string b)
        {
            return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }

        static void TryDelete(string file) { try { if (File.Exists(file)) File.Delete(file); } catch { } }
        static void TryDeleteDir(string dir) { try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { } }
    }

    // ------------------------------------------------------------------
    // The install window
    // ------------------------------------------------------------------
    class InstallForm : Form
    {
        static readonly Color Bg = Color.FromArgb(248, 248, 250);
        static readonly Color Text1 = Color.FromArgb(28, 28, 32);
        static readonly Color Muted = Color.FromArgb(120, 122, 133);
        static readonly Color Accent = Color.FromArgb(79, 107, 237);

        readonly Label lblTitle, lblBody, lblWhere;
        readonly CheckBox chkStartup, chkDesktop, chkLaunch;
        readonly Button btnMain, btnCancel;
        bool done;

        public InstallForm()
        {
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = "Install Work List";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Bg;
            ForeColor = Text1;
            Font = new Font("Segoe UI", 9.75f);
            ClientSize = new Size(440, 300);
            try { Icon = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location); } catch { }

            var logo = new PictureBox { Location = new Point(28, 28), Size = new Size(48, 48), SizeMode = PictureBoxSizeMode.Zoom };
            try { logo.Image = new Icon(Icon, 48, 48).ToBitmap(); } catch { }

            lblTitle = new Label { Text = "Work List", Font = new Font("Segoe UI Semibold", 15f), AutoSize = true, Location = new Point(88, 26) };
            lblBody = new Label
            {
                Text = Installer.IsInstalled
                    ? "Work List is already installed. Installing again updates it. Your tasks are kept."
                    : "A small floating ball that keeps your to-do list one click away.",
                ForeColor = Muted,
                AutoSize = false,
                Location = new Point(90, 58),
                Size = new Size(320, 40),
            };

            chkStartup = new CheckBox { Text = "Start Work List when I sign in to Windows", Checked = true, AutoSize = true, Location = new Point(30, 116) };
            chkDesktop = new CheckBox { Text = "Add a shortcut to my desktop", Checked = false, AutoSize = true, Location = new Point(30, 146) };
            chkLaunch = new CheckBox { Text = "Open Work List now", Checked = true, AutoSize = true, Location = new Point(30, 116), Visible = false };

            lblWhere = new Label
            {
                Text = "Installs just for your Windows account. No admin rights needed.",
                ForeColor = Muted,
                Font = new Font("Segoe UI", 8.5f),
                AutoSize = false,
                Location = new Point(30, 190),
                Size = new Size(390, 36),
            };

            btnMain = new Button { Text = "Install", Size = new Size(110, 34), Location = new Point(310, 246), FlatStyle = FlatStyle.Flat, BackColor = Accent, ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 9.75f) };
            btnMain.FlatAppearance.BorderSize = 0;
            btnMain.Click += OnMain;
            btnCancel = new Button { Text = "Cancel", Size = new Size(96, 34), Location = new Point(204, 246), FlatStyle = FlatStyle.Flat, BackColor = Color.White };
            btnCancel.FlatAppearance.BorderColor = Color.FromArgb(222, 223, 230);
            btnCancel.Click += delegate { Close(); };

            Controls.AddRange(new Control[] { logo, lblTitle, lblBody, chkStartup, chkDesktop, chkLaunch, lblWhere, btnMain, btnCancel });
            AcceptButton = btnMain;
            CancelButton = btnCancel;
        }

        void OnMain(object sender, EventArgs e)
        {
            if (done)
            {
                if (chkLaunch.Checked) { try { Installer.LaunchApp(); } catch { } }
                Close();
                return;
            }

            try
            {
                UseWaitCursor = true;
                btnMain.Enabled = false;
                Installer.Install(chkStartup.Checked, chkDesktop.Checked);
            }
            catch (Exception ex)
            {
                UseWaitCursor = false;
                btnMain.Enabled = true;
                MessageBox.Show(this, "Installation failed:\n\n" + ex.Message, "Work List", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            UseWaitCursor = false;
            done = true;
            Text = "Work List installed";
            lblTitle.Text = "All set!";
            lblBody.Text = "Look for the small ball near the top-right corner of your screen. Click it to open your list.";
            chkStartup.Visible = false;
            chkDesktop.Visible = false;
            chkLaunch.Visible = true;
            lblWhere.Text = "You'll also find Work List in the Start menu. To remove it later: Settings > Apps > Installed apps > Work List > Uninstall.";
            btnCancel.Visible = false;
            btnMain.Text = "Finish";
            btnMain.Enabled = true;
            btnMain.Focus();
        }
    }

    static class Program
    {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        [STAThread]
        static int Main(string[] args)
        {
            Func<string, bool> has = flag => args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
            bool silent = has("/silent");

            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (has("/uninstall")) return RunUninstall(silent);

            if (silent)
            {
                try { Installer.Install(!has("/nostartup"), false); return 0; }
                catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
            }

            Application.Run(new InstallForm());
            return 0;
        }

        static int RunUninstall(bool silent)
        {
            bool deleteTasks = false;
            if (!silent)
            {
                if (MessageBox.Show("Remove Work List from this computer?", "Uninstall Work List",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    return 1;
                deleteTasks = MessageBox.Show(
                        "Also delete your saved tasks?\n\nChoose No to keep them, so they come back if you install Work List again.",
                        "Uninstall Work List", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
            }

            try { Installer.Uninstall(deleteTasks); }
            catch (Exception ex)
            {
                if (!silent) MessageBox.Show("Uninstall failed:\n\n" + ex.Message, "Work List", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }

            if (!silent) MessageBox.Show("Work List has been removed.", "Uninstall Work List", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }
    }
}
