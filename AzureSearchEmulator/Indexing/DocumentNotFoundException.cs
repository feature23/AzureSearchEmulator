namespace AzureSearchEmulator.Indexing;

public class DocumentNotFoundException : Exception
{
    public DocumentNotFoundException()
        : base("The specified document was not found")
    {
    }
}