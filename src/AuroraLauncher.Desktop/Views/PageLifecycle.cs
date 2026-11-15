using System.Windows;
using System.Windows.Threading;

namespace AuroraLauncher.Desktop.Views;

/// <summary>Round-68: "first shown" instead of "first attached".
///
/// Rounds 63–65 hooked expensive init on <c>Loaded</c> with a single-shot
/// guard. That was correct while a page was only attached on demand — but
/// round-68 pre-warms pages during idle (see PageWarmupPolicy), which attaches
/// every page to the tree (as Hidden) long before the user ever clicks it.
/// A Loaded hook would then fire a Modrinth search / Java probe / manifest
/// download for a page nobody opened.
///
/// So the init contract becomes: run ONCE, the first time the page is actually
/// VISIBLE. Pre-warming can build the visual tree and apply templates without
/// any of it.</summary>
internal static class PageLifecycle
{
    /// <summary>Runs <paramref name="onFirstShown"/> exactly once, on the first
    /// transition to Visible. If the page is already visible when this is
    /// called (the page created for the active key), it still runs — after one
    /// dispatcher turn, so the caller's constructor has finished.</summary>
    public static void OnFirstShown(FrameworkElement page, Action onFirstShown)
    {
        var gate = new OnceGate();

        void Run()
        {
            if (!page.IsVisible) return;
            if (!gate.TryEnter()) return;
            onFirstShown();
        }

        page.IsVisibleChanged += (_, _) => Run();
        page.Dispatcher.BeginInvoke(new Action(Run), DispatcherPriority.Background);
    }

    private sealed class OnceGate
    {
        private int _used;

        public bool TryEnter() => System.Threading.Interlocked.Exchange(ref _used, 1) == 0;
    }
}
