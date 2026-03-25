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
        new("Name", "First Name",   "Random first name e.g. John")           { GenerateFunc = f => f.Name.FirstName() },
        new("Name", "Last Name",    "Random last name e.g. Smith")           { GenerateFunc = f => f.Name.LastName() },
        new("Name", "Full Name",    "First + last name")                     { GenerateFunc = f => f.Name.FullName() },
        new("Name", "Prefix",       "Honorific e.g. Mr., Dr., Prof.")        { GenerateFunc = f => f.Name.Prefix() },
        new("Name", "Suffix",       "Name suffix e.g. Jr., Sr., PhD")        { GenerateFunc = f => f.Name.Suffix() },
        new("Name", "Job Title",    "e.g. Senior Software Engineer")         { GenerateFunc = f => f.Name.JobTitle() },

        // ── Internet ────────────────────────────────────────────────────────
        new("Internet", "Email",          "Random email address")              { GenerateFunc = f => f.Internet.Email() },
        new("Internet", "Example Email",  "Email at a safe example domain")    { GenerateFunc = f => f.Internet.ExampleEmail() },
        new("Internet", "Username",       "Random username handle")            { GenerateFunc = f => f.Internet.UserName() },
        new("Internet", "URL",            "Random HTTP URL")                   { GenerateFunc = f => f.Internet.Url() },
        new("Internet", "Password",       "Random password string")            { GenerateFunc = f => f.Internet.Password() },
        new("Internet", "IP Address",     "IPv4 address e.g. 192.168.1.1")    { GenerateFunc = f => f.Internet.Ip() },
        new("Internet", "IPv6 Address",   "IPv6 address")                      { GenerateFunc = f => f.Internet.Ipv6() },
        new("Internet", "MAC Address",    "e.g. 12:34:56:78:9A:BC")           { GenerateFunc = f => f.Internet.Mac() },
        new("Internet", "Domain Name",    "e.g. example.com")                 { GenerateFunc = f => f.Internet.DomainName() },
        new("Internet", "Color",          "Hex RGB color e.g. #a2b3bc")       { GenerateFunc = f => f.Internet.Color() },
        new("Internet", "User Agent",     "Browser user-agent string")         { GenerateFunc = f => f.Internet.UserAgent() },
        new("Internet", "Abbreviation",   "Tech abbreviation e.g. HTTP, SSL")  { GenerateFunc = f => f.Hacker.Abbreviation() },
        new("Internet", "Avatar URL",     "Avatar image URL")                  { GenerateFunc = f => f.Internet.Avatar() },
        new("Internet", "Image URL",      "Random image URL (picsum.photos)")  { GenerateFunc = f => f.Image.PicsumUrl() },

        // ── Phone ───────────────────────────────────────────────────────────
        new("Phone", "Phone Number", "Formatted phone number")               { GenerateFunc = f => f.Phone.PhoneNumber() },

        // ── Address ─────────────────────────────────────────────────────────
        new("Address", "Full Address",        "Complete street address")       { GenerateFunc = f => f.Address.FullAddress() },
        new("Address", "City",                "City name")                     { GenerateFunc = f => f.Address.City() },
        new("Address", "State",               "State or province")             { GenerateFunc = f => f.Address.State() },
        new("Address", "State Abbreviation",  "State abbrev e.g. CA, NY")      { GenerateFunc = f => f.Address.StateAbbr() },
        new("Address", "Country",             "Country name")                  { GenerateFunc = f => f.Address.Country() },
        new("Address", "Country Code",        "ISO 2-letter code e.g. US, GB") { GenerateFunc = f => f.Address.CountryCode() },
        new("Address", "Zip Code",            "Postal / zip code")             { GenerateFunc = f => f.Address.ZipCode() },
        new("Address", "Latitude",            "GPS latitude e.g. 40.712776")   { GenerateFunc = f => f.Address.Latitude().ToString("F6") },
        new("Address", "Longitude",           "GPS longitude e.g. -74.005974") { GenerateFunc = f => f.Address.Longitude().ToString("F6") },

        // ── Lorem ───────────────────────────────────────────────────────────
        new("Lorem", "Word",       "Single random word")                     { GenerateFunc = f => f.Lorem.Word() },
        new("Lorem", "Sentence",   "Random sentence of ~6 words")            { GenerateFunc = f => f.Lorem.Sentence() },
        new("Lorem", "Paragraph",  "Short paragraph (~3 sentences)")         { GenerateFunc = f => f.Lorem.Paragraph() },
        new("Lorem", "Slug",       "URL-safe hyphenated phrase")             { GenerateFunc = f => f.Lorem.Slug() },

        // ── Finance ─────────────────────────────────────────────────────────
        new("Finance", "Amount",           "Decimal monetary amount")            { GenerateFunc = f => f.Finance.Amount().ToString("F2") },
        new("Finance", "Currency Name",    "Currency full name e.g. US Dollar") { GenerateFunc = f => f.Finance.Currency().Description },
        new("Finance", "Currency Code",    "ISO code e.g. USD, EUR, GBP")       { GenerateFunc = f => f.Finance.Currency().Code },
        new("Finance", "IBAN",             "International bank account number")  { GenerateFunc = f => f.Finance.Iban() },
        new("Finance", "Bitcoin",          "Bitcoin wallet address")             { GenerateFunc = f => f.Finance.BitcoinAddress() },
        new("Finance", "Credit Card",      "Credit card number")                 { GenerateFunc = f => f.Finance.CreditCardNumber() },
        new("Finance", "Transaction Type", "e.g. payment, deposit, transfer")   { GenerateFunc = f => f.Finance.TransactionType() },

        // ── Company ─────────────────────────────────────────────────────────
        new("Company", "Company Name",             "Business name")                  { GenerateFunc = f => f.Company.CompanyName() },
        new("Company", "Catch Phrase",             "Marketing catch phrase")         { GenerateFunc = f => f.Company.CatchPhrase() },
        new("Company", "Catch Phrase Adjective",   "e.g. Innovative, Robust")       { GenerateFunc = f => f.Hacker.Adjective() },
        new("Company", "Catch Phrase Descriptor",  "e.g. local, next-generation")   { GenerateFunc = f => f.Hacker.Phrase() },
        new("Company", "Catch Phrase Noun",        "e.g. matrices, interfaces")     { GenerateFunc = f => f.Hacker.Noun() },
        new("Company", "Buzzwords",                "Business buzzword phrase")       { GenerateFunc = f => f.Company.Bs() },

        // ── Date ────────────────────────────────────────────────────────────
        new("Date", "Past Date",      "ISO 8601 date in the past")                  { GenerateFunc = f => f.Date.Past().ToString("o") },
        new("Date", "Future Date",    "ISO 8601 date in the future")                { GenerateFunc = f => f.Date.Future().ToString("o") },
        new("Date", "Recent Date",    "ISO 8601 date within the last day")          { GenerateFunc = f => f.Date.Recent().ToString("o") },
        new("Date", "Birth Date",     "Date of birth e.g. 1985-04-23")              { GenerateFunc = f => f.Date.Past(50).ToString("yyyy-MM-dd") },
        new("Date", "Timestamp",      "Unix timestamp (seconds since epoch)")       { GenerateFunc = _ => DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString() },
        new("Date", "ISO Timestamp",  "Current UTC time as ISO 8601 string")        { GenerateFunc = _ => DateTimeOffset.UtcNow.ToString("o") },

        // ── Random ──────────────────────────────────────────────────────────
        new("Random", "UUID",          "Random UUID / GUID")                 { GenerateFunc = f => f.Random.Guid().ToString() },
        new("Random", "Number",        "Integer between 0 and 100")          { GenerateFunc = f => f.Random.Int(0, 100).ToString() },
        new("Random", "Alpha-Numeric", "Random 10-character alphanumeric")   { GenerateFunc = f => f.Random.AlphaNumeric(10) },
        new("Random", "Hash (MD5)",    "32-character hex hash")              { GenerateFunc = f => f.Random.Hash(32) },
        new("Random", "Boolean",       "true or false")                      { GenerateFunc = f => f.Random.Bool().ToString().ToLowerInvariant() },
        new("Random", "Object ID",     "24-character hex ID (MongoDB-style)"){ GenerateFunc = f => f.Random.Hash(24) },
        new("Random", "Locale",        "Locale code e.g. en_US, de_DE")     { GenerateFunc = f => f.Random.ArrayElement(new[] { "en_US", "en_GB", "fr_FR", "de_DE", "es_ES", "pt_BR", "zh_CN", "ja_JP", "ko_KR", "ru_RU", "ar", "nl", "pl", "sv" }) },

        // ── System ────────────────────────────────────────────────────────
        new("System", "MIME Type",      "e.g. audio/mpeg, image/png")         { GenerateFunc = f => f.System.MimeType() },
        new("System", "File Name",      "Random file name with extension")    { GenerateFunc = f => f.System.FileName() },
        new("System", "File Type",      "File category e.g. audio, video")    { GenerateFunc = f => f.System.FileType() },
        new("System", "File Extension", "e.g. mp3, png, txt")                 { GenerateFunc = f => f.System.FileExt() },
        new("System", "Directory Path", "OS-like directory path")             { GenerateFunc = f => f.System.DirectoryPath() },
        new("System", "File Path",      "Full file path with filename")       { GenerateFunc = f => f.System.FilePath() },
        new("System", "Semver",         "Semantic version e.g. 7.0.5")        { GenerateFunc = f => f.System.Semver() },
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

    /// <summary>
    /// Maps Insomnia/Bogus camelCase field names (e.g. <c>"randomFirstName"</c>,
    /// <c>"name.firstName"</c>) to the nearest catalog entry, or <see langword="null"/>
    /// if no match is found.
    /// </summary>
    public static MockDataEntry? FindByBogusName(string bogusName)
    {
        if (string.IsNullOrEmpty(bogusName)) return null;

        // Lookup table: key is lowercased bogus name / insomnia faker key
        return BogusNameMap.TryGetValue(bogusName.ToLowerInvariant(), out var entry)
            ? entry
            : null;
    }

    // Lazy-initialized lookup: lowercased Bogus/Insomnia method name → catalog entry.
    private static readonly Dictionary<string, MockDataEntry> BogusNameMap = BuildBogusNameMap();

    private static Dictionary<string, MockDataEntry> BuildBogusNameMap()
    {
        // Map well-known Bogus camelCase names and Insomnia faker keys to catalog entries.
        // Multiple aliases can map to the same entry.
        var firstName  = All.First(e => e.Category == "Name"     && e.Field == "First Name");
        var lastName   = All.First(e => e.Category == "Name"     && e.Field == "Last Name");
        var fullName   = All.First(e => e.Category == "Name"     && e.Field == "Full Name");
        var jobTitle   = All.First(e => e.Category == "Name"     && e.Field == "Job Title");
        var email        = All.First(e => e.Category == "Internet" && e.Field == "Email");
        var exampleEmail = All.First(e => e.Category == "Internet" && e.Field == "Example Email");
        var username     = All.First(e => e.Category == "Internet" && e.Field == "Username");
        var url        = All.First(e => e.Category == "Internet" && e.Field == "URL");
        var password   = All.First(e => e.Category == "Internet" && e.Field == "Password");
        var ipAddress  = All.First(e => e.Category == "Internet" && e.Field == "IP Address");
        var domainName = All.First(e => e.Category == "Internet" && e.Field == "Domain Name");
        var phone      = All.First(e => e.Category == "Phone"    && e.Field == "Phone Number");
        var city       = All.First(e => e.Category == "Address"  && e.Field == "City");
        var state      = All.First(e => e.Category == "Address"  && e.Field == "State");
        var country    = All.First(e => e.Category == "Address"  && e.Field == "Country");
        var zipCode    = All.First(e => e.Category == "Address"  && e.Field == "Zip Code");
        var word       = All.First(e => e.Category == "Lorem"    && e.Field == "Word");
        var sentence   = All.First(e => e.Category == "Lorem"    && e.Field == "Sentence");
        var paragraph  = All.First(e => e.Category == "Lorem"    && e.Field == "Paragraph");
        var uuid       = All.First(e => e.Category == "Random"   && e.Field == "UUID");
        var number     = All.First(e => e.Category == "Random"   && e.Field == "Number");
        var boolean    = All.First(e => e.Category == "Random"   && e.Field == "Boolean");
        var pastDate   = All.First(e => e.Category == "Date"     && e.Field == "Past Date");
        var futureDate = All.First(e => e.Category == "Date"     && e.Field == "Future Date");
        var compName   = All.First(e => e.Category == "Company"  && e.Field == "Company Name");
        var creditCard = All.First(e => e.Category == "Finance"  && e.Field == "Credit Card");

        return new Dictionary<string, MockDataEntry>(StringComparer.OrdinalIgnoreCase)
        {
            // Name
            ["randomfirstname"]        = firstName,
            ["name.firstname"]         = firstName,
            ["firstname"]              = firstName,
            ["randomlastname"]         = lastName,
            ["name.lastname"]          = lastName,
            ["lastname"]               = lastName,
            ["fullname"]               = fullName,
            ["name.fullname"]          = fullName,
            ["randomfullname"]         = fullName,
            ["jobtitle"]               = jobTitle,
            ["name.jobtitle"]          = jobTitle,
            // Internet
            ["internet.email"]         = email,
            ["randomemail"]            = email,
            ["email"]                  = email,
            ["internet.exampleemail"]  = exampleEmail,
            ["randomexampleemail"]     = exampleEmail,
            ["exampleemail"]           = exampleEmail,
            ["internet.username"]      = username,
            ["randomusername"]         = username,
            ["username"]               = username,
            ["internet.url"]           = url,
            ["randomurl"]              = url,
            ["url"]                    = url,
            ["internet.password"]      = password,
            ["randompassword"]         = password,
            ["internet.ip"]            = ipAddress,
            ["randomip"]               = ipAddress,
            ["ipaddress"]              = ipAddress,
            ["internet.domainname"]    = domainName,
            ["randomdomainname"]       = domainName,
            // Phone
            ["phone.phonenumber"]      = phone,
            ["randomphonenumber"]      = phone,
            ["phonenumber"]            = phone,
            // Address
            ["address.city"]           = city,
            ["randomcity"]             = city,
            ["city"]                   = city,
            ["address.state"]          = state,
            ["randomstate"]            = state,
            ["state"]                  = state,
            ["address.country"]        = country,
            ["randomcountry"]          = country,
            ["country"]                = country,
            ["address.zipcode"]        = zipCode,
            ["randomzipcode"]          = zipCode,
            ["zipcode"]                = zipCode,
            // Lorem
            ["lorem.word"]             = word,
            ["randomword"]             = word,
            ["lorem.sentence"]         = sentence,
            ["randomsentence"]         = sentence,
            ["lorem.paragraph"]        = paragraph,
            ["randomparagraph"]        = paragraph,
            // Random
            ["random.uuid"]            = uuid,
            ["randomuuid"]             = uuid,
            ["uuid"]                   = uuid,
            ["random.number"]          = number,
            ["randomnumber"]           = number,
            ["random.boolean"]         = boolean,
            ["randomboolean"]          = boolean,
            // Date
            ["date.recent"]            = pastDate,
            ["randomdatepast"]         = pastDate,
            ["date.future"]            = futureDate,
            ["randomdatefuture"]       = futureDate,
            // Company
            ["company.companyname"]    = compName,
            ["randomcompanyname"]      = compName,
            ["companyname"]            = compName,
            // Finance
            ["finance.creditcardnumber"] = creditCard,
            ["creditcardnumber"]       = creditCard,
        };
    }
}
