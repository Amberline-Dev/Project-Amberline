using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Amberline.AirCompanion;

/// <summary>
/// NOT RETAIL (batch 920, owner 20 November 2026: "closer to the website / AIR client"): the setup window and the app's first window in the host
/// pages' look: a navy panel, light text, gold headings, a gold-bordered first button and quieter other buttons, the focus colour on the
/// focused button's border, and a dark title bar. Under a high-contrast theme nothing changes, so Windows' own colours apply (batch 639), as the
/// pages' forced-colours rule does.
/// </summary>
internal static partial class PlayerInstallWindow
{
    /// <summary>The host pages' tokens (<c>HostPages.Look</c>; <c>--line</c> and <c>--gold-dim</c> as the dashboard corrects them, <c>HostPages.HostRules</c>).</summary>
    internal static class Tokens
    {
        internal static readonly Color Panel = ColorTranslator.FromHtml("#0b1728"), Top = ColorTranslator.FromHtml("#13233a"),
            Inner = ColorTranslator.FromHtml("#060e1a"), Bottom = ColorTranslator.FromHtml("#03060c"), Field = ColorTranslator.FromHtml("#040a13"),
            Gold = ColorTranslator.FromHtml("#c9ab6f"), GoldHi = ColorTranslator.FromHtml("#f3e2b3"), Text = ColorTranslator.FromHtml("#e9e2d0"),
            Line = ColorTranslator.FromHtml("#8593a8"), Focus = ColorTranslator.FromHtml("#f2d98e");
    }

    /// <summary>
    /// The look on <paramref name="window"/> before it is shown: <paramref name="gold"/> are its headings, <paramref name="first"/> its first buttons.
    /// False under a high-contrast theme, which keeps Windows' colours.
    /// </summary>
    private static bool Theme(Form window, Control[] gold, params ButtonBase[] first)
    {
        if (SystemInformation.HighContrast) return false;
        window.BackColor = Tokens.Panel;
        window.ForeColor = Tokens.Text;
        window.HandleCreated += (_, _) => DarkTitle(window.Handle);
        foreach (var heading in gold) heading.ForeColor = Tokens.Gold;
        foreach (var control in Descendants(window))
        {
            if (control is ButtonBase button) Paint(button, first.Contains(button));
            else if (control is TextBox box) (box.BackColor, box.ForeColor) = (Tokens.Field, Tokens.Text);
        }
        return true;
    }

    /// <summary>A flat button: gold border and light gold text when first, a grey-blue border otherwise; the focus colour on its border while focused.</summary>
    private static void Paint(ButtonBase button, bool first)
    {
        var rest = first ? Tokens.Gold : Tokens.Line;
        button.FlatStyle = FlatStyle.Flat;
        button.UseVisualStyleBackColor = false;
        button.BackColor = first ? Tokens.Top : Tokens.Inner;
        button.ForeColor = first ? Tokens.GoldHi : Tokens.Text;
        // The border keeps its width while focused, so the button never changes size.
        button.FlatAppearance.BorderSize = button.LogicalToDeviceUnits(first ? 2 : 1);
        button.FlatAppearance.BorderColor = rest;
        button.FlatAppearance.MouseOverBackColor = first ? Tokens.Inner : Tokens.Top;
        button.FlatAppearance.MouseDownBackColor = Tokens.Bottom;
        button.FlatAppearance.CheckedBackColor = Tokens.Top;
        button.GotFocus += (_, _) => button.FlatAppearance.BorderColor = Tokens.Focus;
        button.LostFocus += (_, _) => button.FlatAppearance.BorderColor = rest;
    }

    private static IEnumerable<Control> Descendants(Control parent) =>
        parent.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(Descendants(child)));

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);

    /// <summary>A dark title bar (DWMWA_USE_IMMERSIVE_DARK_MODE, Windows 10 20H1 and later) in the panel's navy (DWMWA_CAPTION_COLOR, Windows 11); older Windows ignores them.</summary>
    private static void DarkTitle(nint window)
    {
        var on = 1;
        var navy = ColorTranslator.ToWin32(Tokens.Panel);
        _ = DwmSetWindowAttribute(window, 20, ref on, sizeof(int));
        _ = DwmSetWindowAttribute(window, 35, ref navy, sizeof(int));
    }

    [DllImport("user32.dll")]
    private static extern nint GetWindowLongPtrW(nint window, int index);

    /// <summary>
    /// DIAGNOSTICS (batch 920): a PNG of a window's inside (no title bar) that is never shown, for review. Making the handles shows nothing (the form
    /// itself is not visible). Windows draws no child of a hidden window (WM_PRINT), and every control of it reads Visible false, so each control whose
    /// own window style and every parent's say visible (WS_VISIBLE) draws itself in turn, parents first.
    /// </summary>
    internal static void Render(Form window, string file)
    {
        // Hiding a control of a hidden window lays nothing out again (it reads hidden already), so each panel lays out every control as if it had
        // changed, the deepest first.
        foreach (var control in Descendants(window).Reverse()) control.Parent!.PerformLayout(control, nameof(Control.Visible));
        window.PerformLayout();
        using var picture = new Bitmap(window.ClientSize.Width, window.ClientSize.Height);
        using (var paint = Graphics.FromImage(picture)) paint.Clear(window.BackColor);
        foreach (var control in Descendants(window).Where(control => control.Width > 0 && control.Height > 0))
        {
            // The panels between have no border, so a control's place in the window is its place in each parent, added up.
            var (shows, place) = (true, Point.Empty);
            for (var at = control; at != window && shows; at = at.Parent!)
                (shows, place) = ((GetWindowLongPtrW(at.Handle, -16) & 0x10000000) != 0, place + (Size)at.Location);
            if (shows) control.DrawToBitmap(picture, new Rectangle(place, control.Size));
        }
        picture.Save(file, ImageFormat.Png);
    }
}
