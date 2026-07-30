namespace Ryujinx.Ava.UI.Models
{
    // [Nextendo] One Switch-style in-game toast: a friend's avatar on the left, a title and a line
    // of text on the right. Shown only while a game is running, for events that happen AFTER launch.
    public class NextendoToastModel
    {
        /// <summary>Monotonic id, used to remove exactly this toast when it auto-expires.</summary>
        public long Id { get; init; }

        /// <summary>Avatar bytes (the friend's picture); null renders an empty circle.</summary>
        public byte[] Image { get; init; }

        public string Title { get; init; } = "";
        public string Text { get; init; } = "";
    }
}
