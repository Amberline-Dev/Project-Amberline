using System.Drawing;
using System.Windows.Forms;

namespace Amberline.AirCompanion;

/// <summary>NOT RETAIL (batch 917): the invite's two windows (<see cref="PlayerInvite"/>), in the installer's style.</summary>
internal static partial class PlayerInstallWindow
{
    internal const string AddButton = "Add";

    /// <summary>"Add &lt;name&gt; (&lt;host&gt;)?" with Yes (the default) and No. True for Yes; No, Escape or closing the dialog add nothing.</summary>
    internal static bool AskAdd(InviteLink.Invite invite)
    {
        var yes = false;
        OnWindowsThread(() =>
        {
            var page = new TaskDialogPage
            {
                Caption = PlayerInvite.Product,
                Heading = $"Add {invite.Name} ({invite.Host})?",
                Text = $"{PlayerInvite.Product} sets up the game for {invite.Name} and adds it to your desktop. " +
                    "Add it only if you know the host.",
                Icon = TaskDialogIcon.Information,
                AllowCancel = true,
                SizeToContent = true,
            };
            page.Buttons.Add(TaskDialogButton.Yes);
            page.Buttons.Add(TaskDialogButton.No);
            page.DefaultButton = TaskDialogButton.Yes;
            yes = TaskDialog.ShowDialog(page) == TaskDialogButton.Yes;
        });
        return yes;
    }

    /// <summary>The welcome window (<see cref="WelcomeWindow"/>): the invite text the player added, or null when they closed it.</summary>
    internal static string? AskInvite()
    {
        string? text = null;
        OnWindowsThread(() =>
        {
            using var window = new WelcomeWindow();
            Application.Run(window);
            text = window.Invite;
        });
        return text;
    }

    /// <summary>
    /// The downloaded app's window. Batch 920 (owner, 20 November 2026; the texts are the orchestrator's): the headline is the Join step in the browser
    /// (<see cref="Headline"/>, then <see cref="Allow"/>); below it, smaller, the fallback (<see cref="Fallback"/>), a paste box and Add. Add takes only
    /// an invite this app can use (<see cref="InviteLink.Decode"/>); otherwise the refusal shows under the box and the window stays.
    /// </summary>
    internal sealed class WelcomeWindow : Form
    {
        internal const string Headline = "Go back to your browser and click Join on the invite page.";
        internal const string Fallback = "If Join does not open anything, paste the invite link here.";
        internal static string Allow => $"Your browser may ask to open {PlayerInvite.Product}. Allow it.";

        private readonly TextBox box;
        private readonly Label problem;

        public string? Invite { get; private set; }

        internal WelcomeWindow()
        {
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = UiFont();
            Text = AccessibleName = PlayerInvite.Product;
            AccessibleDescription = "Adds a server from its invite link.";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            var width = (int)(520 * Font.SizeInPoints / 10F);
            var heading = Line("Join", "", width);
            heading.Font = UiFont(FontStyle.Bold, 2);
            heading.Text = heading.AccessibleDescription = Headline;
            var allow = Line("Allow", "", width);
            allow.Text = allow.AccessibleDescription = Allow;
            var fallback = Line("Paste instead", "", width);
            fallback.Text = fallback.AccessibleDescription = Fallback;
            fallback.Margin = new Padding(0, 22, 12, 4);
            box = Named(new TextBox { Width = width - 16, Margin = Padding.Empty, Location = new Point(6, 5) }, "Invite link", "Paste the invite link the host sent you.");
            // Batch 920: the pages' field: the box on the field colour with room around the text, in a two-pixel border (--line, --focus while it has the focus).
            var field = Named(new Panel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(6, 5, 6, 5), Margin = Padding.Empty, Location = new Point(2, 2) },
                "Invite link field", "The field around the invite link box.");
            field.Controls.Add(box);
            var ring = Named(new Panel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(2), Margin = new Padding(0, 2, 0, 4) },
                "Invite link box", "The border of the invite link box.");
            ring.Controls.Add(field);
            problem = Line("Problem", "Why the invite link cannot be used.", width);
            problem.Font = UiFont(FontStyle.Bold);
            problem.Visible = false;
            var add = Action("&" + AddButton, AddButton, "Adds the server from the invite link.");
            var close = Action("Close", "Close", "Closes this window.");
            var rows = Rows((heading, null), (allow, null), (fallback, null), (ring, null), (problem, null), (Buttons(close, add), null));
            Named(rows, PlayerInvite.Product, AccessibleDescription);
            Controls.Add(rows);
            AcceptButton = add;
            CancelButton = close;
            // The Join step is the first thing to do, so no button here is gold: the fallback stays quiet.
            if (Theme(this, [heading]))
            {
                box.BorderStyle = BorderStyle.None;
                (field.BackColor, ring.BackColor) = (Tokens.Field, Tokens.Line);
                box.GotFocus += (_, _) => ring.BackColor = Tokens.Focus;
                box.LostFocus += (_, _) => ring.BackColor = Tokens.Line;
            }
            add.Click += (_, _) =>
            {
                if (InviteLink.Decode(box.Text).Refusal is { } refusal)
                {
                    problem.Text = problem.AccessibleDescription = refusal;
                    problem.Visible = true;
                    box.Select();
                    return;
                }
                Invite = box.Text;
                Close();
            };
            close.Click += (_, _) => Close();
            ResumeLayout(false);
            PerformLayout();
        }
    }
}
