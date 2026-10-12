using Android.Content;
using Lumi.Mobile.Services;

namespace Lumi.Mobile.Android;

/// <summary>Shares answer text through the system share sheet.</summary>
internal sealed class AndroidTextShareService(Context context) : ITextShareService
{
    // Intent extras travel through a ~1 MB binder transaction; leave headroom for the chooser.
    private const int MaxSharedCharacters = 100_000;

    private readonly Context _context = context.ApplicationContext ?? context;

    public bool IsAvailable => true;

    public bool TryShare(string text, string? subject)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var send = new Intent(Intent.ActionSend)
            .SetType("text/plain")
            .PutExtra(Intent.ExtraText, text.Length > MaxSharedCharacters ? text[..MaxSharedCharacters] : text);
        if (!string.IsNullOrWhiteSpace(subject))
            send.PutExtra(Intent.ExtraSubject, subject);

        if (Intent.CreateChooser(send, (string?)null) is not { } chooser)
            return false;

        chooser.AddFlags(ActivityFlags.NewTask);
        try
        {
            _context.StartActivity(chooser);
            return true;
        }
        catch (ActivityNotFoundException)
        {
            return false;
        }
    }
}
