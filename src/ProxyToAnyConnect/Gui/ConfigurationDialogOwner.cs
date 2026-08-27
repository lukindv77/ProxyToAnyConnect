namespace ProxyToAnyConnect.Gui;

internal sealed class ConfigurationDialogOwner
{
    private readonly object _sync = new();
    private Action? _cancelActive;
    private int _stopped;

    public DialogResult Run(Form dialog, IWin32Window owner)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        ArgumentNullException.ThrowIfNull(owner);

        return Run(
            () => dialog.ShowDialog(owner),
            () => RequestClose(dialog));
    }

    public TResult Run<TResult>(
        Func<TResult> showDialog,
        Action cancelDialog)
    {
        ArgumentNullException.ThrowIfNull(showDialog);
        ArgumentNullException.ThrowIfNull(cancelDialog);

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _stopped) != 0, this);
            if (_cancelActive is not null)
            {
                throw new InvalidOperationException(
                    "A configuration dialog is already owned by the active GUI command generation.");
            }

            _cancelActive = cancelDialog;
        }

        try
        {
            return showDialog();
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_cancelActive, cancelDialog))
                {
                    _cancelActive = null;
                }
            }
        }
    }

    public void Stop()
    {
        Action? cancelActive;
        lock (_sync)
        {
            if (Volatile.Read(ref _stopped) != 0)
            {
                return;
            }

            Volatile.Write(ref _stopped, 1);
            cancelActive = _cancelActive;
            _cancelActive = null;
        }

        // Never invoke arbitrary UI cancellation while holding the ownership lock.
        // Closing a modal window pumps nested WinForms messages and can synchronously
        // unwind the exact Run() call that currently owns this callback.
        cancelActive?.Invoke();
    }

    private static void RequestClose(Form dialog)
    {
        if (dialog.IsDisposed || dialog.Disposing)
        {
            return;
        }

        void CloseCore()
        {
            if (dialog.IsDisposed || dialog.Disposing)
            {
                return;
            }

            dialog.DialogResult = DialogResult.Cancel;
            dialog.Close();
        }

        if (!dialog.InvokeRequired)
        {
            CloseCore();
            return;
        }

        try
        {
            dialog.BeginInvoke((Action)CloseCore);
        }
        catch (InvalidOperationException) when (dialog.IsDisposed || dialog.Disposing || !dialog.IsHandleCreated)
        {
            // The modal already lost its handle while Stop raced natural completion.
        }
        catch (ObjectDisposedException)
        {
            // The exact dialog completed between the ownership check and BeginInvoke.
        }
    }
}
