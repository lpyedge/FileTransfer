internal static class FileOperationErrorHandler
{
    public static void Log(ILogger logger, Exception exception, string operation, string path, string? context = null)
    {
        if (string.IsNullOrWhiteSpace(context))
        {
            logger.LogError(exception, LogText.Get("OperationFailed"), operation, path);
        }
        else
        {
            logger.LogError(exception, LogText.Get("OperationFailedWithContext"), operation, path, context);
        }
    }
}
