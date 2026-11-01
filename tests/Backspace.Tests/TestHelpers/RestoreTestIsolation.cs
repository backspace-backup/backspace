using System;
using System.Linq;
using System.Threading.Tasks;
using Backspace.Services;

namespace Backspace.Tests.TestHelpers;

internal static class RestoreTestIsolation
{
    internal static async Task ClearCompletedAsync(Action pump)
    {
        pump();
        if (ServiceLocator.ActiveRestores.HasRunning)
            throw new InvalidOperationException("The preceding scenario left a restore running.");
        foreach (var session in ServiceLocator.ActiveRestores.Sessions.ToArray())
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (session.IsDismissing)
                await WaitForDismissal();
            if (!ServiceLocator.ActiveRestores.Sessions.Contains(session)) continue;
            var dismissal = ServiceLocator.ActiveRestores.DismissAsync(session);
            while (!dismissal.IsCompleted)
                await WaitForDismissal();
            await dismissal;
            if (ServiceLocator.ActiveRestores.Sessions.Contains(session))
                throw new InvalidOperationException("The preceding scenario's result could not be dismissed: " + session.ErrorMessage);

            async Task WaitForDismissal()
            {
                if (DateTime.UtcNow >= deadline)
                    throw new InvalidOperationException("The preceding scenario's result dismissal did not finish: " + session.BackupName);
                await Task.Delay(20);
                pump();
            }
        }
    }
}
