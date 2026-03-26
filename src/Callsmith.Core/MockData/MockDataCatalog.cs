using Bogus;

namespace Callsmith.Core.MockData;

/// <summary>
/// A single entry in the mock data catalog.
/// Carries the category name, field name, human-readable description, and the
/// Bogus delegate that produces a sample value.
/// </summary>
public sealed record MockDataEntry(string Category, string Field, string Description)
{
    // Internal so that the Bogus dependency stays contained to Core.
    internal Func<Faker, string> GenerateFunc { get; init; } = _ => string.Empty;
}

/// <summary>
/// Static catalog of all Bogus-backed mock data generators available for use in
/// environment variable segments.
/// </summary>
public static class MockDataCatalog
{
    /// <summary>All available mock data entries, ordered by category then field.</summary>
    public static IReadOnlyList<MockDataEntry> All { get; } =
    [
        // ── Name ────────────────────────────────────────────────────────────
        new("Name", "First Name",   "Random first name e.g. John")                { GenerateFunc = f => f.Name.FirstName() },
        new("Name", "Last Name",    "Random last name e.g. Smith")                { GenerateFunc = f => f.Name.LastName() },
        new("Name", "Full Name",    "First + last name")                          { GenerateFunc = f => f.Name.FullName() },
        new("Name", "Prefix",       "Honorific e.g. Mr., Dr., Prof.")             { GenerateFunc = f => f.Name.Prefix() },
        new("Name", "Suffix",       "Name suffix e.g. Jr., Sr., PhD")             { GenerateFunc = f => f.Name.Suffix() },
        new("Name", "Job Title",    "e.g. Senior Software Engineer")              { GenerateFunc = f => f.Name.JobTitle() },        
        new("Name", "Job Area",       "e.g. Applications, Integration")           { GenerateFunc = f => f.Name.JobArea() },
        new("Name", "Job Descriptor", "e.g. Senior, Legacy")                      { GenerateFunc = f => f.Name.JobDescriptor() },
        new("Name", "Job Type",       "e.g. Designer, Supervisor")                { GenerateFunc = f => f.Name.JobType() },
        // ── Internet ────────────────────────────────────────────────────────
        new("Internet", "Email",          "Random email address")                 { GenerateFunc = f => f.Internet.Email() },
        new("Internet", "Example Email",  "Email at a safe example domain")       { GenerateFunc = f => f.Internet.ExampleEmail() },
        new("Internet", "Username",       "Random username handle")               { GenerateFunc = f => f.Internet.UserName() },
        new("Internet", "URL",            "Random HTTP URL")                      { GenerateFunc = f => f.Internet.Url() },
        new("Internet", "Password",       "Random password string")               { GenerateFunc = f => f.Internet.Password() },
        new("Internet", "IP Address",     "IPv4 address e.g. 192.168.1.1")        { GenerateFunc = f => f.Internet.Ip() },
        new("Internet", "IPv6 Address",   "IPv6 address")                         { GenerateFunc = f => f.Internet.Ipv6() },
        new("Internet", "MAC Address",    "e.g. 12:34:56:78:9A:BC")               { GenerateFunc = f => f.Internet.Mac() },
        new("Internet", "Domain Name",    "e.g. example.com")                     { GenerateFunc = f => f.Internet.DomainName() },
        new("Internet", "Color",          "Hex RGB color e.g. #a2b3bc")         { GenerateFunc = f => f.Internet.Color() },
        new("Internet", "User Agent",     "Browser user-agent string")            { GenerateFunc = f => f.Internet.UserAgent() },
        new("Internet", "Abbreviation",   "Tech abbreviation e.g. HTTP, SSL")     { GenerateFunc = f => f.Hacker.Abbreviation() },
        new("Internet", "Avatar URL",     "Avatar image URL")                     { GenerateFunc = f => f.Internet.Avatar() },
        new("Internet", "Image URL",      "Random image URL (picsum.photos)")     { GenerateFunc = f => f.Image.PicsumUrl() },        
        new("Internet", "Protocol",       "http or https")                        { GenerateFunc = f => f.Internet.Protocol() },
        new("Internet", "Domain Suffix",  "e.g. .com, .net, .org")                { GenerateFunc = f => f.Internet.DomainSuffix() },
        new("Internet", "Domain Word",    "Single domain word e.g. example")      { GenerateFunc = f => f.Internet.DomainWord() },
        // ── Phone ───────────────────────────────────────────────────────────
        new("Phone", "Phone Number", "Formatted phone number")                    { GenerateFunc = f => f.Phone.PhoneNumber() },

        // ── Address ─────────────────────────────────────────────────────────
        new("Address", "Full Address",        "Complete street address")          { GenerateFunc = f => f.Address.FullAddress() },
        new("Address", "City",                "City name")                        { GenerateFunc = f => f.Address.City() },
        new("Address", "State",               "State or province")                { GenerateFunc = f => f.Address.State() },
        new("Address", "State Abbreviation",  "State abbrev e.g. CA, NY")         { GenerateFunc = f => f.Address.StateAbbr() },
        new("Address", "Country",             "Country name")                     { GenerateFunc = f => f.Address.Country() },
        new("Address", "Country Code",        "ISO 2-letter code e.g. US, GB")    { GenerateFunc = f => f.Address.CountryCode() },
        new("Address", "Zip Code",            "Postal / zip code")                { GenerateFunc = f => f.Address.ZipCode() },
        new("Address", "Latitude",            "GPS latitude e.g. 40.712776")      { GenerateFunc = f => f.Address.Latitude().ToString("F6") },
        new("Address", "Longitude",           "GPS longitude e.g. -74.005974")    { GenerateFunc = f => f.Address.Longitude().ToString("F6") },        
        new("Address", "Street Name",         "e.g. Main Street, Oak Ave")        { GenerateFunc = f => f.Address.StreetName() },
        new("Address", "Street Address",      "e.g. 123 Main St")                 { GenerateFunc = f => f.Address.StreetAddress() },
        // ── Lorem ───────────────────────────────────────────────────────────
        new("Lorem", "Word",       "Single random word")                          { GenerateFunc = f => f.Lorem.Word() },
        new("Lorem", "Words",      "Multiple random words")                       { GenerateFunc = f => string.Join(" ", f.Lorem.Words(3)) },
        new("Lorem", "Sentence",   "Random sentence of ~6 words")                 { GenerateFunc = f => f.Lorem.Sentence() },
        new("Lorem", "Sentences",  "Multiple sentences")                          { GenerateFunc = f => f.Lorem.Sentences(2) },
        new("Lorem", "Paragraph",  "Short paragraph (~3 sentences)")              { GenerateFunc = f => f.Lorem.Paragraph() },
        new("Lorem", "Paragraphs", "Multiple paragraphs")                         { GenerateFunc = f => f.Lorem.Paragraphs(2) },
        new("Lorem", "Text",       "Random text block")                           { GenerateFunc = f => f.Lorem.Text() },
        new("Lorem", "Lines",      "Multiple lines of lorem text")                { GenerateFunc = f => f.Lorem.Lines(3) },
        new("Lorem", "Slug",       "URL-safe hyphenated phrase")                  { GenerateFunc = f => f.Lorem.Slug() },

        // ── Finance ─────────────────────────────────────────────────────────
        new("Finance", "Amount",           "Decimal monetary amount")             { GenerateFunc = f => f.Finance.Amount().ToString("F2") },
        new("Finance", "Currency Name",    "Currency full name e.g. US Dollar")   { GenerateFunc = f => f.Finance.Currency().Description },
        new("Finance", "Currency Code",    "ISO code e.g. USD, EUR, GBP")         { GenerateFunc = f => f.Finance.Currency().Code },
        new("Finance", "IBAN",             "International bank account number")   { GenerateFunc = f => f.Finance.Iban() },
        new("Finance", "Bitcoin",          "Bitcoin wallet address")              { GenerateFunc = f => f.Finance.BitcoinAddress() },
        new("Finance", "Credit Card",      "Credit card number")                  { GenerateFunc = f => f.Finance.CreditCardNumber() },
        new("Finance", "Credit Card Mask",  "Masked card e.g. xxxx-1234")         { GenerateFunc = f => $"****-****-****-{f.Random.Int(1000, 9999)}" },
        new("Finance", "Transaction Type", "e.g. payment, deposit, transfer")     { GenerateFunc = f => f.Finance.TransactionType() },
        new("Finance", "Account Number",   "Bank account number")                 { GenerateFunc = f => f.Finance.Account() },
        new("Finance", "Account Name",     "Bank account name")                   { GenerateFunc = f => f.Finance.AccountName() },
        new("Finance", "BIC",              "Bank Identifier Code")                { GenerateFunc = f => f.Finance.Bic() },
        new("Finance", "Currency Symbol",  "e.g. $, \u20ac, \u00a3")              { GenerateFunc = f => f.Finance.Currency().Symbol },

        // ── Company ─────────────────────────────────────────────────────────
        new("Company", "Company Name",             "Business name")               { GenerateFunc = f => f.Company.CompanyName() },
        new("Company", "Catch Phrase",             "Marketing catch phrase")      { GenerateFunc = f => f.Company.CatchPhrase() },
        new("Company", "Catch Phrase Adjective",   "e.g. Innovative, Robust")     { GenerateFunc = f => f.Hacker.Adjective() },
        new("Company", "Catch Phrase Descriptor",  "e.g. local, next-generation") { GenerateFunc = f => f.Hacker.Phrase() },
        new("Company", "Catch Phrase Noun",        "e.g. matrices, interfaces")   { GenerateFunc = f => f.Hacker.Noun() },
        new("Company", "Buzzwords",                "Business buzzword phrase")    { GenerateFunc = f => f.Company.Bs() },

        // ── Date ────────────────────────────────────────────────────────────
        new("Date", "Past Date",      "ISO 8601 date in the past")                { GenerateFunc = f => f.Date.Past().ToString("o") },
        new("Date", "Future Date",    "ISO 8601 date in the future")              { GenerateFunc = f => f.Date.Future().ToString("o") },
        new("Date", "Recent Date",    "ISO 8601 date within the last day")        { GenerateFunc = f => f.Date.Recent().ToString("o") },
        new("Date", "Birth Date",     "Date of birth e.g. 1985-04-23")            { GenerateFunc = f => f.Date.Past(50).ToString("yyyy-MM-dd") },
        new("Date", "Timestamp",      "Unix timestamp (seconds since epoch)")     { GenerateFunc = _ => DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString() },
        new("Date", "ISO Timestamp",  "Current UTC time as ISO 8601 string")      { GenerateFunc = _ => DateTimeOffset.UtcNow.ToString("o") },
        new("Date", "Weekday",        "e.g. Monday, Wednesday")                   { GenerateFunc = f => f.Date.Weekday() },
        new("Date", "Month",          "e.g. January, March")                      { GenerateFunc = f => f.Date.Month() },
        // ── Random ──────────────────────────────────────────────────────────
        new("Random", "UUID",          "Random UUID / GUID")                      { GenerateFunc = f => f.Random.Guid().ToString() },
        new("Random", "Number",        "Integer between 0 and 100")               { GenerateFunc = f => f.Random.Int(0, 100).ToString() },
        new("Random", "Alpha-Numeric", "Random 10-character alphanumeric")        { GenerateFunc = f => f.Random.AlphaNumeric(10) },
        new("Random", "Hash (MD5)",    "32-character hex hash")                   { GenerateFunc = f => f.Random.Hash(32) },
        new("Random", "Boolean",       "true or false")                           { GenerateFunc = f => f.Random.Bool().ToString().ToLowerInvariant() },
        new("Random", "Object ID",     "24-character hex ID (MongoDB-style)")     { GenerateFunc = f => f.Random.Hash(24) },
        new("Random", "Locale",        "Locale code e.g. en_US, de_DE")           { GenerateFunc = f => f.Random.ArrayElement(["en_US", "en_GB", "fr_FR", "de_DE", "es_ES", "pt_BR", "zh_CN", "ja_JP", "ko_KR", "ru_RU", "ar", "nl", "pl", "sv"]) },

        // ── System ────────────────────────────────────────────────────────
        new("System", "MIME Type",      "e.g. audio/mpeg, image/png")             { GenerateFunc = f => f.System.MimeType() },
        new("System", "File Name",      "Random file name with extension")        { GenerateFunc = f => f.System.FileName() },
        new("System", "File Type",      "File category e.g. audio, video")        { GenerateFunc = f => f.System.FileType() },
        new("System", "File Extension", "e.g. mp3, png, txt")                     { GenerateFunc = f => f.System.FileExt() },
        new("System", "Directory Path", "OS-like directory path")                 { GenerateFunc = f => f.System.DirectoryPath() },
        new("System", "File Path",             "Full file path with filename")    { GenerateFunc = f => f.System.FilePath() },
        new("System", "Semver",                "Semantic version e.g. 7.0.5")     { GenerateFunc = f => f.System.Semver() },
        new("System", "Common File Name",      "e.g. budget.doc, report.pdf")     { GenerateFunc = f => f.System.CommonFileName() },
        new("System", "Common File Type",      "e.g. application, audio, text")   { GenerateFunc = f => f.System.CommonFileType() },
        new("System", "Common File Extension", "e.g. doc, jpg, pdf, mp3")         { GenerateFunc = f => f.System.CommonFileExt() },

        // ── Commerce ────────────────────────────────────────────────────────────
        new("Commerce", "Price",             "Product price e.g. 9.99")           { GenerateFunc = f => f.Commerce.Price() },
        new("Commerce", "Product",           "Product category e.g. Computer")    { GenerateFunc = f => f.Commerce.Product() },
        new("Commerce", "Product Name",      "Full product name")                 { GenerateFunc = f => f.Commerce.ProductName() },
        new("Commerce", "Product Adjective", "e.g. Fantastic, Ergonomic")         { GenerateFunc = f => f.Commerce.ProductAdjective() },
        new("Commerce", "Product Material",  "e.g. Cotton, Wooden")               { GenerateFunc = f => f.Commerce.ProductMaterial() },
        new("Commerce", "Department",        "e.g. Toys & Games, Electronics")    { GenerateFunc = f => f.Commerce.Department() },

        // ── Database ────────────────────────────────────────────────────────────
        new("Database", "Column",    "DB column name e.g. createdAt")             { GenerateFunc = f => f.Database.Column() },
        new("Database", "Type",      "SQL type e.g. varchar, int")                { GenerateFunc = f => f.Database.Type() },
        new("Database", "Collation", "e.g. utf8_general_ci")                      { GenerateFunc = f => f.Database.Collation() },
        new("Database", "Engine",    "e.g. InnoDB, MyISAM")                       { GenerateFunc = f => f.Database.Engine() },

        // ── Hacker ──────────────────────────────────────────────────────────────
        new("Hacker", "Adjective", "e.g. neural, wireless")                       { GenerateFunc = f => f.Hacker.Adjective() },
        new("Hacker", "Noun",      "e.g. protocol, array")                        { GenerateFunc = f => f.Hacker.Noun() },
        new("Hacker", "Verb",      "e.g. connect, compress")                      { GenerateFunc = f => f.Hacker.Verb() },
        new("Hacker", "Ingverb",   "e.g. calculating, synthesizing")              { GenerateFunc = f => f.Hacker.IngVerb() },
        new("Hacker", "Phrase",    "Tech phrase e.g. 'Use virtual ...' ")         { GenerateFunc = f => f.Hacker.Phrase() },
    ];

    /// <summary>All distinct category names in their natural display order.</summary>
    public static IReadOnlyList<string> Categories { get; } =
        All.Select(e => e.Category).Distinct().ToList();

    /// <summary>Returns all entries for the given category.</summary>
    public static IReadOnlyList<MockDataEntry> GetFields(string category) =>
        All.Where(e => e.Category == category).ToList();

    /// <summary>
    /// Generates a single value for the given category/field pair.
    /// A fresh <see cref="Faker"/> is created per call to ensure thread safety.
    /// </summary>
    public static string Generate(string category, string field)
    {
        var entry = All.FirstOrDefault(e => e.Category == category && e.Field == field);
        if (entry is null) return string.Empty;
        return entry.GenerateFunc(new Faker());
    }
}
