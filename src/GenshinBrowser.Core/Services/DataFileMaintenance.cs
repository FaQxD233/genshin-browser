namespace GenshinBrowser.Services;

public static class DataFileMaintenance
{
    public static void PurgeStaleTempFiles(string applicationDataRoot)
    {
        JsonFileWriter.PurgeStaleTempFiles(applicationDataRoot);
    }
}
