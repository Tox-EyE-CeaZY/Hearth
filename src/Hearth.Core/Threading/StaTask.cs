namespace Hearth.Core.Threading;

/// <summary>
/// Runs work on a single-threaded-apartment thread.
///
/// The shell's COM objects are apartment-threaded. Handing enumeration to
/// <c>Task.Run</c> puts it on a thread-pool thread, which is MTA, and the
/// marshalling that COM then has to do either fails outright or misbehaves in
/// ways that surface far from the cause. Anything touching IShellItem,
/// IEnumShellItems or IShellItemImageFactory belongs here.
/// </summary>
public static class StaTask
{
    public static Task<T> Run<T>(Func<T> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            try { completion.SetResult(work()); }
            catch (Exception ex) { completion.SetException(ex); }
        })
        {
            // Background so a hung shell call can never keep the process alive.
            IsBackground = true,
            Name = "Hearth.Sta",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return completion.Task;
    }

    public static Task Run(Action work) =>
        Run<object?>(() => { work(); return null; });
}
