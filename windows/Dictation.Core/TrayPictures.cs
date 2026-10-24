namespace Dictation.Core;

/// <summary>
/// The notification-area icon's pictures, drawn once per state for the inks
/// in use, and only those: a change of inks disposes every picture drawn
/// with the old ones.
/// </summary>
/// <remarks>
/// Each picture is a GDI icon handle on Windows. The inks change with the
/// taskbar's theme, with High Contrast and with every change of a contrast
/// theme's colours, so a cache kept per (state, inks) without trimming would
/// gain up to one handle per state at each change and free none until the
/// app quits. At most one set, one picture per state, is held. Plain
/// <c>net9.0</c> and generic over the picture, so the rule is tested on Linux.
/// </remarks>
/// <typeparam name="TPicture">What a picture is on this platform.</typeparam>
/// <param name="draw">Draws the picture for a state in the given inks.</param>
public sealed class TrayPictures<TPicture>(Func<TrayIcon, TrayInks, TPicture> draw) : IDisposable
    where TPicture : class, IDisposable
{
    private readonly Func<TrayIcon, TrayInks, TPicture> _draw = draw ?? throw new ArgumentNullException(nameof(draw));
    private readonly Dictionary<TrayIcon, TPicture> _drawn = [];
    private TrayInks? _inks;

    /// <summary>How many pictures are held.</summary>
    public int Count => _drawn.Count;

    /// <summary>
    /// The picture for <paramref name="state"/> in <paramref name="inks"/>,
    /// drawn the first time it is asked for. Asking in other inks than the
    /// last call's disposes every picture drawn before, the one on screen
    /// included, so the caller puts the picture this returns up in the same
    /// turn, before anything can draw the old one again.
    /// </summary>
    public TPicture For(TrayIcon state, TrayInks inks)
    {
        if (_inks != inks)
        {
            Forget();
            _inks = inks;
        }

        if (!_drawn.TryGetValue(state, out var picture))
        {
            picture = _draw(state, inks);
            _drawn[state] = picture;
        }

        return picture;
    }

    public void Dispose() => Forget();

    private void Forget()
    {
        foreach (var picture in _drawn.Values)
        {
            picture.Dispose();
        }

        _drawn.Clear();
    }
}
