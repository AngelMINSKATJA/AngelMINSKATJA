using System;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace BarcodeTray.Tests.Support;

/// <summary>Exécute du code sur un thread STA (requis par le presse-papier Windows) ; xUnit utilise des threads MTA.</summary>
internal static class Sta
{
    internal static T Run<T>(Func<T> func, int timeoutSeconds = 60)
    {
        T result = default!;
        ExceptionDispatchInfo? error = null;

        var thread = new Thread(() =>
        {
            try
            {
                result = func();
            }
            catch (Exception ex)
            {
                error = ExceptionDispatchInfo.Capture(ex);
            }
        })
        {
            IsBackground = true,
            Name = "Thread STA de test",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!thread.Join(TimeSpan.FromSeconds(timeoutSeconds)))
        {
            throw new TimeoutException("Le thread STA n'a pas terminé en " + timeoutSeconds + " s.");
        }

        error?.Throw();
        return result;
    }

    internal static void Run(Action action, int timeoutSeconds = 60)
    {
        Run<object?>(() =>
        {
            action();
            return null;
        }, timeoutSeconds);
    }
}
