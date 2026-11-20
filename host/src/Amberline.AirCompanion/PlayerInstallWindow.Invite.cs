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
                Text = $"{PlayerInvite.Product} sets up the game to play on {invite.Name} and puts it on your desktop. " +
                    "Add it only if you know its host.",
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
    /// The downloaded app's window: "Now click Join on your invite page, or paste the invite link here", a paste box and Add. Add takes only an
    /// invite this app can use (<see cref="InviteLink.Decode"/>); otherwise the refusal shows under the box and the window stays.
    /// </summary>
    internal sealed class WelcomeWindow : Form
    {
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
            var heading = Line("Welcome", $"{PlayerInvite.Product} is ready.", width);
            heading.Font = UiFont(FontStyle.Bold, 2);
            heading.Text = $"{PlayerInvite.Product} is ready";
            var intro = Line("What to do", "", width);
            intro.Text = intro.AccessibleDescription = "Now click Join on your invite page, or paste the invite link here.";
            box = Named(new TextBox { Width = width, Margin = new Padding(0, 6, 0, 6) }, "Invite link", "Paste the invite link the host sent you.");
            problem = Line("Problem", "Why the invite link can't be used.", width);
            problem.Font = UiFont(FontStyle.Bold);
            problem.Visible = false;
            var add = Action("&" + AddButton, AddButton, "Adds the server from the invite link.");
            var close = Action("Close", "Close", "Closes this window.");
            var rows = Rows((heading, null), (intro, null), (box, null), (problem, null), (Buttons(close, add), null));
            Named(rows, PlayerInvite.Product, AccessibleDescription);
            Controls.Add(rows);
            AcceptButton = add;
            CancelButton = close;
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
