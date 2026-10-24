using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using NasReader.Server;

namespace NasReader;

public class MainForm : Form
{
    readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(14, 16, 21) };
    readonly string _url;
    FormWindowState _prevState;
    FormBorderStyle _prevBorder;

    public MainForm(string url)
    {
        _url = url;
        Text = "Nas Reader";
        BackColor = Color.FromArgb(14, 16, 21);
        MinimumSize = new Size(900, 600);
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1500, 950);
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }
        Controls.Add(_web);
        Load += async (_, _) => await InitAsync();
        HandleCreated += (_, _) => UseDarkTitleBar();
    }

    async Task InitAsync()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Paths.Root, "WebView2"));
            await _web.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "Le composant Microsoft Edge WebView2 est introuvable.\n\nInstalle-le depuis https://go.microsoft.com/fwlink/p/?LinkId=2124703\n\n" + ex.Message,
                "Nas Reader", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
            return;
        }

        var core = _web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = true;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.ContainsFullScreenElementChanged += (_, _) => SetFullScreen(core.ContainsFullScreenElement);
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri) { UseShellExecute = true }); } catch { }
        };
        core.Navigate(_url);
    }

    void SetFullScreen(bool on)
    {
        if (on)
        {
            _prevState = WindowState;
            _prevBorder = FormBorderStyle;
            FormBorderStyle = FormBorderStyle.None;
            if (WindowState == FormWindowState.Maximized) WindowState = FormWindowState.Normal;
            WindowState = FormWindowState.Maximized;
        }
        else
        {
            FormBorderStyle = _prevBorder;
            WindowState = _prevState;
        }
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    void UseDarkTitleBar()
    {
        int on = 1;
        DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int));
        int caption = 0x00150E0E; // COLORREF (BGR) of the app background
        DwmSetWindowAttribute(Handle, 35, ref caption, sizeof(int));
    }
}
