namespace GameDevUsageBar.Infrastructure;
internal static class ErrorObserver
{
    public static void Report(Action<Exception>? observer,Exception error)
    {
        try{observer?.Invoke(error);}catch{/* Optional diagnostic observers must never alter the operation. */}
    }
}
