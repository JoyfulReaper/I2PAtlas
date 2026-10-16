namespace DN42Atlas.AddressBook;

public sealed class AddressBookParseException : FormatException
{
    public string SourceName { get; }
    public int LineNumber { get; }

    internal AddressBookParseException(string source, int lineNumber, string reason)
        : base($"Addressbook '{source}', line {lineNumber}: {reason}")
    {
        SourceName = source;
        LineNumber = lineNumber;
    }
}
