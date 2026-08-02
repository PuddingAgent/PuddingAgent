namespace PuddingDesktop.Storage;

public interface IDataRootSafetyValidator
{
    ValidatedDataRoot ValidateDataRoot(string dataRoot);

    ValidatedDataRoot ValidateLogRoot(
        string dataRoot,
        bool requireLogDirectory);

    bool IsDescendantOf(string candidatePath, string parentPath);
}
