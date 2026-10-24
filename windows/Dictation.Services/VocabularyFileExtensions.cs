using System.Collections.Frozen;

namespace Dictation.Services;

/// <summary>
/// The file extensions a dotted token is allowed to end in and still NOT be a
/// host name.
/// </summary>
/// <remarks>
/// <para>
/// <b>This list exists because a file name is shaped exactly like a domain and
/// a domain is unconditionally distinctive.</b>
/// <c>VocabularyTermExtractor.LooksLikeDomain</c> accepts any
/// <c>name.ext</c> whose last part is 2–6 lowercase letters, and
/// <see cref="VocabularyDistinctiveness"/> maps
/// <see cref="VocabularyTermKind.Domain"/> to true with no dictionary and no
/// identifier check behind it. Measured through the engine:
/// <c>Extract("Please read README.md and package.json before running
/// setup.exe or main.py.")</c> offered all four as distinctive Domain terms.
/// The Code source reads <c>.md</c> and <c>.cs</c>, where a file name recurs on
/// nearly every page, so those arrive with an occurrence count in the
/// hundreds — and distinctiveness is the FIRST ranking key, so they sit at the
/// head of the offer, selected by default, spending slots out of a 64-phrase
/// budget.
/// </para>
/// <para>
/// It is the same defect class the "last part must be lowercase" tightening
/// was written to kill. That rule removed <c>StringComparison.OrdinalIgnoreCase</c>
/// and every other dotted member-access chain by demanding a lowercase tail —
/// and a file extension is lowercase, so file names walked straight through it.
/// </para>
/// <para>
/// <b>What it costs, stated plainly.</b> Several entries here are also
/// country-code top-level domains: <c>.md</c> is Moldova, <c>.py</c> Paraguay,
/// <c>.js</c> Jersey, <c>.sh</c> Saint Helena, <c>.cs</c> a retired code. A
/// host name ending in one of those is no longer offered. That is the trade,
/// and the ratio is not close: in the corpora this feature reads —
/// a checkout, a Documents folder, a notes folder — <c>README.md</c> appears
/// constantly and <c>anything.md</c> as a Moldovan host appears never. The
/// commercial TLDs a name really uses are deliberately absent from this list:
/// <c>.com</c>, <c>.ai</c>, <c>.io</c>, <c>.co</c>, <c>.org</c>, <c>.net</c>
/// and <c>.dev</c> are not extensions here, so <c>example.ai</c> and
/// <c>example.com</c> are unaffected.
/// </para>
/// </remarks>
public static class VocabularyFileExtensions
{
    private static readonly FrozenSet<string> Extensions = new[]
    {
        // Every extension a vocabulary scan reads (mail, code, notes and
        // documents), because a scan of a folder is guaranteed to mention the
        // names of the files inside it.
        "eml",
        "swift", "py", "ts", "tsx", "js", "cs", "md", "h", "m",
        "txt", "text", "markdown", "csv",
        "json",

        // The rest of what a README, a build script or a support email names
        // out loud. Binaries and archives first — nothing here is ever read by
        // the scanner, which is exactly why the name is all that ever appears.
        "exe", "dll", "so", "dylib", "zip", "gz", "tar", "7z", "msi", "pdb",

        // Images and documents.
        "png", "jpg", "jpeg", "gif", "svg", "webp", "ico", "bmp",
        "pdf", "docx", "xlsx", "pptx", "rtf",

        // Configuration and markup a repository is full of.
        "yaml", "yml", "xml", "html", "htm", "css", "scss", "toml", "ini",
        "cfg", "conf", "lock", "props", "csproj", "sln", "gradle", "plist",

        // Scripts and logs.
        "sh", "bat", "cmd", "ps1", "psm1", "rb", "go", "rs", "java", "kt",
        "php", "sql", "log", "bak", "tmp",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many extensions the list holds. Traceable, not asserted.</summary>
    public static int Count => Extensions.Count;

    /// <summary>
    /// Is this dot-part a file extension? Case-insensitive, without the dot.
    /// </summary>
    public static bool Contains(ReadOnlySpan<char> extension) =>
        extension.Length > 0 && Extensions.GetAlternateLookup<ReadOnlySpan<char>>().Contains(extension);
}
