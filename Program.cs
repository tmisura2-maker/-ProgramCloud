using Npgsql;
using Dapper;
using Newtonsoft.Json;
using System.Security.Cryptography;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

// Postavi WebRootPath
var possibleRoots = new[] {
    Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "wwwroot"),
    Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "ProgramCloud", "wwwroot"),
    Path.Combine(AppContext.BaseDirectory, "wwwroot"),
    Path.Combine(Directory.GetCurrentDirectory(), "wwwroot")
};
Console.WriteLine($"[ProgramCloud] BaseDirectory: {AppContext.BaseDirectory}");
Console.WriteLine($"[ProgramCloud] CWD: {Directory.GetCurrentDirectory()}");
foreach (var root in possibleRoots)
{
    var fullPath = Path.GetFullPath(root);
    var hasIndex = File.Exists(Path.Combine(fullPath, "index.html"));
    Console.WriteLine($"[ProgramCloud] Provjera: {fullPath} -> exists={Directory.Exists(fullPath)}, hasIndex={hasIndex}");
    if (Directory.Exists(fullPath) && hasIndex)
    {
        builder.Environment.WebRootPath = fullPath;
        Console.WriteLine($"[ProgramCloud] ✓ WebRoot ODABRAN: {fullPath}");
        break;
    }
}
if (string.IsNullOrEmpty(builder.Environment.WebRootPath))
    Console.WriteLine("[ProgramCloud] ✗ NIJEDAN wwwroot nije pronađen!");

var app = builder.Build();

// PostgreSQL connection string
var connStr = "Host=c2zpzn2yhknf7kl9xs0xo06g;Port=5432;Database=postgres;Username=postgres;Password=Nt0nww9wBu799C321fv4DFbWNMT9G137MTUUzrycQHvQaCfhjO18fQdDPqCJUCH1";
Console.WriteLine($"[ProgramCloud] Database: PostgreSQL");
InitDatabase(connStr);

app.UseStaticFiles();

// ==================== HELPER ====================
string HashPassword(string pw)
{
    using var sha = SHA256.Create();
    return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(pw)));
}

string GenerateToken() => Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");

int? GetUserId(HttpContext ctx)
{
    var token = ctx.Request.Headers["Authorization"].FirstOrDefault()?.Replace("Bearer ", "");
    if (string.IsNullOrEmpty(token)) return null;
    using var conn = new NpgsqlConnection(connStr);
    var user = conn.QueryFirstOrDefault<dynamic>(
        "SELECT \"Id\" FROM \"Users\" WHERE \"Token\" = @token", new { token });
    return user == null ? null : (int?)user.Id;
}

bool IsAdmin(HttpContext ctx)
{
    var token = ctx.Request.Headers["Authorization"].FirstOrDefault()?.Replace("Bearer ", "");
    if (string.IsNullOrEmpty(token)) return false;
    using var conn = new NpgsqlConnection(connStr);
    var user = conn.QueryFirstOrDefault<dynamic>(
        "SELECT \"IsAdmin\" FROM \"Users\" WHERE \"Token\" = @token", new { token });
    return user != null && user.IsAdmin;
}

int EnsureCashRegister(NpgsqlConnection conn, string oib, string companyName, string address, string city, string postalCode, string iban, string taxModel, string spaceCode, string registerCode)
{
    var company = conn.QueryFirstOrDefault<dynamic>("SELECT \"Id\" FROM \"Companies\" WHERE \"OIB\" = @oib", new { oib });
    int companyId;
    if (company == null)
    {
        companyId = conn.QuerySingle<int>("INSERT INTO \"Companies\" (\"OIB\", \"Name\", \"Address\", \"PostalCode\", \"City\", \"IBAN\", \"TaxModel\", \"CreatedAt\") VALUES (@oib, @name, @addr, @pc, @city, @iban, @tm, @now) RETURNING \"Id\"",
            new { oib, name = companyName, addr = address ?? "", pc = postalCode ?? "", city = city ?? "", iban = iban ?? "", tm = taxModel ?? "R1", now = DateTime.Now.ToString("o") });
        Console.WriteLine($"[SYNC] Nova firma: {companyName} (OIB: {oib})");
    }
    else
    {
        companyId = (int)company.Id;
        conn.Execute("UPDATE \"Companies\" SET \"Name\"=@name, \"Address\"=@addr, \"PostalCode\"=@pc, \"City\"=@city, \"IBAN\"=@iban, \"TaxModel\"=@tm WHERE \"Id\"=@id",
            new { name = companyName, addr = address ?? "", pc = postalCode ?? "", city = city ?? "", iban = iban ?? "", tm = taxModel ?? "R1", id = companyId });
    }

    var sc = string.IsNullOrEmpty(spaceCode) ? "1" : spaceCode;
    var space = conn.QueryFirstOrDefault<dynamic>(
        "SELECT \"Id\" FROM \"BusinessSpaces\" WHERE \"CompanyId\" = @cid AND \"Code\" = @code", new { cid = companyId, code = sc });
    int spaceId;
    if (space == null)
    {
        spaceId = conn.QuerySingle<int>("INSERT INTO \"BusinessSpaces\" (\"CompanyId\", \"Code\", \"Name\", \"IsActive\", \"CreatedAt\") VALUES (@cid, @code, @name, true, @now) RETURNING \"Id\"",
            new { cid = companyId, code = sc, name = "PP " + sc, now = DateTime.Now.ToString("o") });
        Console.WriteLine($"[SYNC] Novi PP: {sc} za firmu {companyName}");
    }
    else
    {
        spaceId = (int)space.Id;
    }

    var rc = string.IsNullOrEmpty(registerCode) ? "1" : registerCode;
    var register = conn.QueryFirstOrDefault<dynamic>(
        "SELECT \"Id\" FROM \"CashRegisters\" WHERE \"BusinessSpaceId\" = @sid AND \"Code\" = @code", new { sid = spaceId, code = rc });
    int registerId;
    if (register == null)
    {
        var licKey = Guid.NewGuid().ToString("N").Substring(0, 16).ToUpper();
        registerId = conn.QuerySingle<int>("INSERT INTO \"CashRegisters\" (\"BusinessSpaceId\", \"Code\", \"LicenseKey\", \"IsActive\", \"CreatedAt\") VALUES (@sid, @code, @key, true, @now) RETURNING \"Id\"",
            new { sid = spaceId, code = rc, key = licKey, now = DateTime.Now.ToString("o") });
        Console.WriteLine($"[SYNC] Nova blagajna: {rc} u PP {sc}");
    }
    else
    {
        registerId = (int)register.Id;
    }

    return registerId;
}

// ==================== AUTH ====================

app.MapPost("/api/auth/login", async (HttpRequest req) =>
{
    var body = await new StreamReader(req.Body).ReadToEndAsync();
    var data = JsonConvert.DeserializeObject<LoginRequest>(body);
    if (data == null) return Results.BadRequest("Nedostaju podaci");

    using var conn = new NpgsqlConnection(connStr);
    var hash = HashPassword(data.Password ?? "");
    var user = conn.QueryFirstOrDefault<dynamic>(
        "SELECT * FROM \"Users\" WHERE \"Username\" = @u AND \"PasswordHash\" = @h",
        new { u = data.Username, h = hash });
    if (user == null)
        return Results.Json(new { success = false, message = "Pogrešno korisničko ime ili lozinka" });

    var token = GenerateToken();
    conn.Execute("UPDATE \"Users\" SET \"Token\" = @token WHERE \"Id\" = @id", new { token, id = (int)user.Id });

    return Results.Json(new { success = true, token, username = (string)user.Username,
        isAdmin = (bool)user.IsAdmin, displayName = (string)user.DisplayName });
});

app.MapGet("/api/auth/me", (HttpContext ctx) =>
{
    var token = ctx.Request.Headers["Authorization"].FirstOrDefault()?.Replace("Bearer ", "");
    if (string.IsNullOrEmpty(token)) return Results.Unauthorized();
    using var conn = new NpgsqlConnection(connStr);
    var user = conn.QueryFirstOrDefault<dynamic>(
        "SELECT \"Id\", \"Username\", \"DisplayName\", \"IsAdmin\" FROM \"Users\" WHERE \"Token\" = @token", new { token });
    if (user == null) return Results.Unauthorized();
    return Results.Json(new { id = (int)user.Id, username = (string)user.Username,
        displayName = (string)user.DisplayName, isAdmin = (bool)user.IsAdmin });
});

app.MapPost("/api/auth/logout", (HttpContext ctx) =>
{
    var token = ctx.Request.Headers["Authorization"].FirstOrDefault()?.Replace("Bearer ", "");
    if (!string.IsNullOrEmpty(token))
    {
        using var conn = new NpgsqlConnection(connStr);
        conn.Execute("UPDATE \"Users\" SET \"Token\" = NULL WHERE \"Token\" = @token", new { token });
    }
    return Results.Ok(new { message = "Odjavljeni ste" });
});

// ==================== ADMIN: KORISNICI ====================

app.MapGet("/api/admin/users", (HttpContext ctx) =>
{
    if (!IsAdmin(ctx)) return Results.Unauthorized();
    using var conn = new NpgsqlConnection(connStr);
    var users = conn.Query("SELECT \"Id\", \"Username\", \"DisplayName\", \"IsAdmin\", \"CreatedAt\" FROM \"Users\" ORDER BY \"Username\"");
    return Results.Json(users);
});

app.MapPost("/api/admin/users", async (HttpContext ctx) =>
{
    if (!IsAdmin(ctx)) return Results.Unauthorized();
    var body = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
    var data = JsonConvert.DeserializeObject<UserData>(body);
    if (data == null || string.IsNullOrEmpty(data.Username) || string.IsNullOrEmpty(data.Password))
        return Results.BadRequest("Nedostaju podaci");

    using var conn = new NpgsqlConnection(connStr);
    var exists = conn.QueryFirstOrDefault<int>("SELECT COUNT(*) FROM \"Users\" WHERE \"Username\" = @u", new { u = data.Username });
    if (exists > 0) return Results.BadRequest("Korisnik već postoji");

    conn.Execute(@"INSERT INTO ""Users"" (""Username"", ""PasswordHash"", ""DisplayName"", ""IsAdmin"", ""CreatedAt"")
        VALUES (@u, @h, @d, @a, @now)",
        new { u = data.Username, h = HashPassword(data.Password), d = data.DisplayName ?? data.Username,
            a = data.IsAdmin, now = DateTime.Now.ToString("o") });
    return Results.Ok(new { message = "Korisnik kreiran" });
});

app.MapPut("/api/admin/users/{id}", async (int id, HttpContext ctx) =>
{
    if (!IsAdmin(ctx)) return Results.Unauthorized();
    var body = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
    var data = JsonConvert.DeserializeObject<UserData>(body);
    if (data == null) return Results.BadRequest();

    using var conn = new NpgsqlConnection(connStr);
    if (!string.IsNullOrEmpty(data.Password))
        conn.Execute("UPDATE \"Users\" SET \"PasswordHash\" = @h WHERE \"Id\" = @id", new { h = HashPassword(data.Password), id });
    if (!string.IsNullOrEmpty(data.DisplayName))
        conn.Execute("UPDATE \"Users\" SET \"DisplayName\" = @d WHERE \"Id\" = @id", new { d = data.DisplayName, id });
    conn.Execute("UPDATE \"Users\" SET \"IsAdmin\" = @a WHERE \"Id\" = @id", new { a = data.IsAdmin, id });
    return Results.Ok(new { message = "Ažurirano" });
});

app.MapDelete("/api/admin/users/{id}", (int id, HttpContext ctx) =>
{
    if (!IsAdmin(ctx)) return Results.Unauthorized();
    using var conn = new NpgsqlConnection(connStr);
    conn.Execute("DELETE FROM \"UserCompanies\" WHERE \"UserId\" = @id", new { id });
    conn.Execute("DELETE FROM \"Users\" WHERE \"Id\" = @id", new { id });
    return Results.Ok(new { message = "Obrisano" });
});

// ==================== ADMIN: FIRME ====================

app.MapGet("/api/admin/companies", (HttpContext ctx) =>
{
    if (!IsAdmin(ctx)) return Results.Unauthorized();
    using var conn = new NpgsqlConnection(connStr);
    var list = conn.Query("SELECT * FROM \"Companies\" ORDER BY \"Name\"");
    return Results.Json(list);
});

app.MapPost("/api/admin/companies", async (HttpContext ctx) =>
{
    if (!IsAdmin(ctx)) return Results.Unauthorized();
    var body = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
    var data = JsonConvert.DeserializeObject<CompanyData>(body);
    if (data == null || string.IsNullOrEmpty(data.OIB)) return Results.BadRequest("Nedostaje OIB");

    using var conn = new NpgsqlConnection(connStr);
    conn.Execute(@"INSERT INTO ""Companies"" (""OIB"", ""Name"", ""Address"", ""City"", ""CreatedAt"")
        VALUES (@oib, @name, @addr, @city, @now)",
        new { oib = data.OIB, name = data.Name, addr = data.Address ?? "", city = data.City ?? "",
            now = DateTime.Now.ToString("o") });
    return Results.Ok(new { message = "Firma dodana" });
});

// ==================== ADMIN: KORISNIK ↔ FIRMA ====================

app.MapGet("/api/admin/users/{userId}/companies", (int userId, HttpContext ctx) =>
{
    if (!IsAdmin(ctx)) return Results.Unauthorized();
    using var conn = new NpgsqlConnection(connStr);
    var list = conn.Query(@"SELECT c.* FROM ""Companies"" c
        JOIN ""UserCompanies"" uc ON c.""Id"" = uc.""CompanyId"" WHERE uc.""UserId"" = @userId", new { userId });
    return Results.Json(list);
});

app.MapPost("/api/admin/users/{userId}/companies/{companyId}", (int userId, int companyId, HttpContext ctx) =>
{
    if (!IsAdmin(ctx)) return Results.Unauthorized();
    using var conn = new NpgsqlConnection(connStr);
    var exists = conn.QueryFirstOrDefault<int>(
        "SELECT COUNT(*) FROM \"UserCompanies\" WHERE \"UserId\"=@u AND \"CompanyId\"=@c", new { u = userId, c = companyId });
    if (exists > 0) return Results.Ok(new { message = "Već povezano" });
    conn.Execute("INSERT INTO \"UserCompanies\" (\"UserId\", \"CompanyId\") VALUES (@u, @c)", new { u = userId, c = companyId });
    return Results.Ok(new { message = "Povezano" });
});

app.MapDelete("/api/admin/users/{userId}/companies/{companyId}", (int userId, int companyId, HttpContext ctx) =>
{
    if (!IsAdmin(ctx)) return Results.Unauthorized();
    using var conn = new NpgsqlConnection(connStr);
    conn.Execute("DELETE FROM \"UserCompanies\" WHERE \"UserId\"=@u AND \"CompanyId\"=@c", new { u = userId, c = companyId });
    return Results.Ok(new { message = "Uklonjeno" });
});

// ==================== ADMIN: POSLOVNI PROSTORI & BLAGAJNE ====================

app.MapGet("/api/admin/companies/{companyId}/spaces", (int companyId, HttpContext ctx) =>
{
    if (!IsAdmin(ctx)) return Results.Unauthorized();
    using var conn = new NpgsqlConnection(connStr);
    var list = conn.Query("SELECT * FROM \"BusinessSpaces\" WHERE \"CompanyId\" = @companyId ORDER BY \"Code\"", new { companyId });
    return Results.Json(list);
});

app.MapGet("/api/admin/spaces/{spaceId}/registers", (int spaceId, HttpContext ctx) =>
{
    if (!IsAdmin(ctx)) return Results.Unauthorized();
    using var conn = new NpgsqlConnection(connStr);
    var list = conn.Query("SELECT * FROM \"CashRegisters\" WHERE \"BusinessSpaceId\" = @spaceId ORDER BY \"Code\"", new { spaceId });
    return Results.Json(list);
});

// ==================== BRISANJE BLAGAJNE ====================

app.MapDelete("/api/admin/registers/{registerId}", (int registerId, HttpContext ctx) =>
{
    if (!IsAdmin(ctx)) return Results.Unauthorized();
    using var conn = new NpgsqlConnection(connStr);
    conn.Open();
    
    var orderCount = conn.QueryFirstOrDefault<int>("SELECT COUNT(*) FROM \"Orders\" WHERE \"CashRegisterId\" = @rid", new { rid = registerId });
    conn.Execute("DELETE FROM \"Orders\" WHERE \"CashRegisterId\" = @rid", new { rid = registerId });
    conn.Execute("DELETE FROM \"CashRegisters\" WHERE \"Id\" = @rid", new { rid = registerId });
    
    Console.WriteLine($"[ADMIN] Obrisana blagajna {registerId} sa {orderCount} racuna");
    return Results.Json(new { success = true, message = $"Obrisana blagajna sa {orderCount} racuna" });
});

// ==================== BLAGAJNA SYNC ====================

app.MapPost("/api/sync/order", async (HttpRequest req) =>
{
    var body = await new StreamReader(req.Body).ReadToEndAsync();
    var data = JsonConvert.DeserializeObject<SyncOrderRequest>(body);
    if (data == null || string.IsNullOrEmpty(data.CompanyOIB))
        return Results.BadRequest("Nedostaje OIB firme");

    using var conn = new NpgsqlConnection(connStr);
    conn.Open();

    Console.WriteLine($"[SYNC] Primljeno: OIB={data.CompanyOIB}, Addr={data.CompanyAddress}, City={data.CompanyCity}, PC={data.CompanyPostalCode}, IBAN={data.CompanyIBAN}");
    var registerId = EnsureCashRegister(conn, data.CompanyOIB, data.CompanyName ?? "",
        data.CompanyAddress ?? "", data.CompanyCity ?? "",
        data.CompanyPostalCode ?? "", data.CompanyIBAN ?? "", data.CompanyTaxModel ?? "R1",
        data.BusinessSpaceCode ?? "1", data.CashRegisterCode ?? "1");

    var o = data.Order;
    if (o == null)
        return Results.BadRequest("Nedostaje račun");

    var exists = conn.QueryFirstOrDefault<int>(
        "SELECT COUNT(*) FROM \"Orders\" WHERE \"CashRegisterId\" = @rid AND \"LocalOrderId\" = @localId",
        new { rid = registerId, localId = o.LocalOrderId });
    if (exists > 0)
        return Results.Json(new { success = true, inserted = 0, message = "Račun već postoji" });

    conn.Execute(@"INSERT INTO ""Orders"" (""CashRegisterId"", ""LocalOrderId"", ""ReceiptNumber"", ""Total"", ""PaymentMethod"",
        ""UserName"", ""CustomerName"", ""CustomerOib"", ""CustomerAddress"", ""CustomerCity"", ""Status"", ""CreatedAt"", ""CompletedAt"", ""ItemsJson"", ""TipAmount"", ""IsFiscalized"", ""JIR"", ""ZKI"", ""FiscalizedAt"")
        VALUES (@rid, @localId, @receiptNum, @total, @payment, @user, @customer, @customerOib, @custAddr, @custCity, @status,
        @created, @completed, @items, @tip, @fisc, @jir, @zki, @fiscAt)",
        new { rid = registerId, localId = o.LocalOrderId, receiptNum = o.ReceiptNumber,
            total = o.Total, payment = o.PaymentMethod, user = o.UserName,
            customer = o.CustomerName, customerOib = o.CustomerOib,
            custAddr = o.CustomerAddress, custCity = o.CustomerCity, status = o.Status,
            created = o.CreatedAt, completed = o.CompletedAt,
            items = JsonConvert.SerializeObject(o.Items),
            tip = o.TipAmount, fisc = o.IsFiscalized != 0, jir = o.JIR, zki = o.ZKI,
            fiscAt = o.FiscalizedAt });

    Console.WriteLine($"[SYNC] Račun #{o.ReceiptNumber} od {data.CompanyName} ({data.CompanyOIB}) - {o.Total:F2} EUR");
    return Results.Json(new { success = true, inserted = 1, message = "Račun primljen" });
});

// Ažuriraj fiskalizacijske podatke (JIR/ZKI) za postojeći račun
app.MapPut("/api/sync/order/fiscalize", async (HttpRequest req) =>
{
    var body = await new StreamReader(req.Body).ReadToEndAsync();
    var data = JsonConvert.DeserializeObject<FiscalizeUpdateRequest>(body);
    if (data == null || string.IsNullOrEmpty(data.CompanyOIB))
        return Results.BadRequest("Nedostaju podaci");

    using var conn = new NpgsqlConnection(connStr);
    conn.Open();

    // Pronađi blagajnu
    var company = conn.QueryFirstOrDefault<dynamic>("SELECT \"Id\" FROM \"Companies\" WHERE \"OIB\" = @oib", new { oib = data.CompanyOIB });
    if (company == null) return Results.NotFound("Firma nije pronađena");

    var sc = string.IsNullOrEmpty(data.BusinessSpaceCode) ? "1" : data.BusinessSpaceCode;
    var rc = string.IsNullOrEmpty(data.CashRegisterCode) ? "1" : data.CashRegisterCode;

    var register = conn.QueryFirstOrDefault<dynamic>(@"
        SELECT cr.""Id"" FROM ""CashRegisters"" cr
        JOIN ""BusinessSpaces"" bs ON cr.""BusinessSpaceId"" = bs.""Id""
        WHERE bs.""CompanyId"" = @cid AND bs.""Code"" = @sc AND cr.""Code"" = @rc",
        new { cid = (int)company.Id, sc, rc });

    if (register == null) return Results.NotFound("Blagajna nije pronađena");

    var updated = conn.Execute(@"
        UPDATE ""Orders"" SET ""IsFiscalized"" = true, ""JIR"" = @jir, ""ZKI"" = @zki, ""FiscalizedAt"" = @fiscAt,
            ""ReceiptNumber"" = COALESCE(NULLIF(@receiptNum, 0), ""ReceiptNumber"")
        WHERE ""CashRegisterId"" = @rid AND ""LocalOrderId"" = @localId",
        new { jir = data.JIR, zki = data.ZKI, fiscAt = data.FiscalizedAt,
            receiptNum = data.ReceiptNumber, rid = (int)register.Id, localId = data.LocalOrderId });

    Console.WriteLine($"[SYNC] Fiskalizacija račun #{data.LocalOrderId}: JIR={data.JIR}, updated={updated}");
    return Results.Json(new { success = true, updated, message = "Fiskalizacija ažurirana" });
});

// ==================== COMPANY DETAIL ====================

app.MapGet("/api/admin/companies/{companyId}/detail", (int companyId, HttpContext ctx) =>
{
    if (!IsAdmin(ctx)) return Results.Unauthorized();
    using var conn = new NpgsqlConnection(connStr);
    var comp = conn.QueryFirstOrDefault<dynamic>("SELECT * FROM \"Companies\" WHERE \"Id\" = @id", new { id = companyId });
    if (comp == null) return Results.NotFound();

    var spaces = conn.Query<dynamic>("SELECT * FROM \"BusinessSpaces\" WHERE \"CompanyId\" = @cid ORDER BY \"Code\"", new { cid = companyId });
    var spaceList = new List<Dictionary<string, object>>();
    foreach (var s in spaces)
    {
        var regs = conn.Query<dynamic>("SELECT * FROM \"CashRegisters\" WHERE \"BusinessSpaceId\" = @sid ORDER BY \"Code\"", new { sid = (int)s.Id });
        var regList = regs.Select(r => new Dictionary<string, object> {
            {"Id", (int)r.Id}, {"Code", (string)r.Code}, {"LicenseKey", (string)(r.LicenseKey ?? "")},
            {"IsActive", (bool)r.IsActive}, {"RegistrationDate", (string)(r.RegistrationDate ?? "")}, {"CreatedAt", (string)(r.CreatedAt ?? "")}
        }).ToList();
        spaceList.Add(new Dictionary<string, object> {
            {"Id", (int)s.Id}, {"Code", (string)s.Code}, {"Name", (string)(s.Name ?? s.Code)}, {"Registers", regList}
        });
    }

    var result = new Dictionary<string, object> {
        {"Id", (int)comp.Id}, {"Name", (string)comp.Name}, {"OIB", (string)comp.OIB},
        {"Address", (string)(comp.Address ?? "")}, {"PostalCode", (string)(comp.PostalCode ?? "")},
        {"City", (string)(comp.City ?? "")}, {"IBAN", (string)(comp.IBAN ?? "")},
        {"TaxModel", (string)(comp.TaxModel ?? "R1")}, {"CreatedAt", (string)(comp.CreatedAt ?? "")}, {"Spaces", spaceList}
    };
    return Results.Json(result);
});

app.MapGet("/api/admin/companies/{companyId}/orders", (int companyId, HttpContext ctx, string? from, string? to, int? receiptNumber, string? zki, string? jir, string? customer, bool? nonFiscalized, int? registerId) =>
{
    if (!IsAdmin(ctx)) return Results.Unauthorized();
    using var conn = new NpgsqlConnection(connStr);

    var sql = @"SELECT o.*, cr.""Code"" as ""RegisterCode"", bs.""Code"" as ""SpaceCode""
        FROM ""Orders"" o
        JOIN ""CashRegisters"" cr ON o.""CashRegisterId"" = cr.""Id""
        JOIN ""BusinessSpaces"" bs ON cr.""BusinessSpaceId"" = bs.""Id""
        WHERE bs.""CompanyId"" = @cid";
    var p = new DynamicParameters();
    p.Add("cid", companyId);
    if (registerId.HasValue) { sql += @" AND o.""CashRegisterId"" = @rid"; p.Add("rid", registerId.Value); }
    if (!string.IsNullOrEmpty(from)) { sql += @" AND o.""CompletedAt"" >= @from"; p.Add("from", from); }
    if (!string.IsNullOrEmpty(to)) { sql += @" AND o.""CompletedAt"" <= @to"; p.Add("to", to + "T23:59:59"); }
    if (receiptNumber.HasValue) { sql += @" AND o.""ReceiptNumber"" = @rn"; p.Add("rn", receiptNumber.Value); }
    if (!string.IsNullOrEmpty(zki)) { sql += @" AND o.""ZKI"" LIKE @zki"; p.Add("zki", "%" + zki + "%"); }
    if (!string.IsNullOrEmpty(jir)) { sql += @" AND o.""JIR"" LIKE @jir"; p.Add("jir", "%" + jir + "%"); }
    if (!string.IsNullOrEmpty(customer)) { sql += @" AND (o.""CustomerName"" LIKE @cust OR o.""CustomerOib"" LIKE @cust)"; p.Add("cust", "%" + customer + "%"); }
    if (nonFiscalized == true) { sql += @" AND o.""IsFiscalized"" = false"; }
    sql += @" ORDER BY o.""ReceiptNumber"" DESC LIMIT 500";
    return Results.Json(conn.Query(sql, p));
});

// ==================== DASHBOARD & ORDERS ====================

app.MapGet("/api/admin/orders/{orderId}", (int orderId, HttpContext ctx) =>
{
    if (!IsAdmin(ctx)) return Results.Unauthorized();
    using var conn = new NpgsqlConnection(connStr);
    var o = conn.QueryFirstOrDefault<dynamic>(@"SELECT o.*, cr.""Code"" as ""RegisterCode"", bs.""Code"" as ""SpaceCode"",
        c.""Name"" as ""CompanyName"", c.""OIB"", c.""Address"" as ""CompanyAddress"", c.""PostalCode"" as ""CompanyPostalCode"",
        c.""City"" as ""CompanyCity"", c.""IBAN"" as ""CompanyIBAN"", c.""TaxModel"" as ""CompanyTaxModel""
        FROM ""Orders"" o
        JOIN ""CashRegisters"" cr ON o.""CashRegisterId"" = cr.""Id""
        JOIN ""BusinessSpaces"" bs ON cr.""BusinessSpaceId"" = bs.""Id""
        JOIN ""Companies"" c ON bs.""CompanyId"" = c.""Id""
        WHERE o.""Id"" = @id", new { id = orderId });
    if (o == null) return Results.NotFound();
    return Results.Json(o);
});

app.MapGet("/api/dashboard", (HttpContext ctx) =>
{
    var userId = GetUserId(ctx);
    if (userId == null) return Results.Unauthorized();
    using var conn = new NpgsqlConnection(connStr);
    var isAdm = IsAdmin(ctx);
    var today = DateTime.Today.ToString("yyyy-MM-dd");

    string sql;
    object param;
    if (isAdm)
    {
        sql = @"SELECT c.""Id"" as ""CompanyId"", c.""Name"" as ""CompanyName"", c.""OIB"",
            bs.""Id"" as ""SpaceId"", bs.""Code"" as ""SpaceCode"",
            cr.""Id"" as ""RegisterId"", cr.""Code"" as ""RegisterCode"", cr.""IsActive"",
            COUNT(o.""Id"") as ""OrderCount"", COALESCE(SUM(o.""Total""), 0) as ""TotalRevenue""
            FROM ""Companies"" c
            JOIN ""BusinessSpaces"" bs ON c.""Id"" = bs.""CompanyId""
            JOIN ""CashRegisters"" cr ON bs.""Id"" = cr.""BusinessSpaceId""
            LEFT JOIN ""Orders"" o ON cr.""Id"" = o.""CashRegisterId"" AND o.""CompletedAt"" >= @today
            GROUP BY cr.""Id"", c.""Id"", c.""Name"", c.""OIB"", bs.""Id"", bs.""Code"", cr.""Code"", cr.""IsActive""
            ORDER BY c.""Name"", bs.""Code"", cr.""Code""";
        param = new { today };
    }
    else
    {
        sql = @"SELECT c.""Id"" as ""CompanyId"", c.""Name"" as ""CompanyName"", c.""OIB"",
            bs.""Id"" as ""SpaceId"", bs.""Code"" as ""SpaceCode"",
            cr.""Id"" as ""RegisterId"", cr.""Code"" as ""RegisterCode"", cr.""IsActive"",
            COUNT(o.""Id"") as ""OrderCount"", COALESCE(SUM(o.""Total""), 0) as ""TotalRevenue""
            FROM ""Companies"" c
            JOIN ""UserCompanies"" uc ON c.""Id"" = uc.""CompanyId"" AND uc.""UserId"" = @userId
            JOIN ""BusinessSpaces"" bs ON c.""Id"" = bs.""CompanyId""
            JOIN ""CashRegisters"" cr ON bs.""Id"" = cr.""BusinessSpaceId""
            LEFT JOIN ""Orders"" o ON cr.""Id"" = o.""CashRegisterId"" AND o.""CompletedAt"" >= @today
            GROUP BY cr.""Id"", c.""Id"", c.""Name"", c.""OIB"", bs.""Id"", bs.""Code"", cr.""Code"", cr.""IsActive""
            ORDER BY c.""Name"", bs.""Code"", cr.""Code""";
        param = new { userId = userId.Value, today };
    }
    return Results.Json(conn.Query(sql, param));
});

app.MapGet("/api/orders", (HttpContext ctx, int? registerId, string? from, string? to, string? oib) =>
{
    var userId = GetUserId(ctx);
    if (userId == null) return Results.Unauthorized();
    using var conn = new NpgsqlConnection(connStr);
    var isAdm = IsAdmin(ctx);

    var sql = @"SELECT o.*, cr.""Code"" as ""RegisterCode"", bs.""Code"" as ""SpaceCode"", c.""Name"" as ""CompanyName"", c.""OIB""
        FROM ""Orders"" o
        JOIN ""CashRegisters"" cr ON o.""CashRegisterId"" = cr.""Id""
        JOIN ""BusinessSpaces"" bs ON cr.""BusinessSpaceId"" = bs.""Id""
        JOIN ""Companies"" c ON bs.""CompanyId"" = c.""Id""";
    if (!isAdm) sql += @" JOIN ""UserCompanies"" uc ON c.""Id"" = uc.""CompanyId"" AND uc.""UserId"" = @userId";
    sql += " WHERE 1=1";

    var p = new DynamicParameters();
    if (!isAdm) p.Add("userId", userId.Value);
    if (registerId.HasValue) { sql += @" AND o.""CashRegisterId"" = @rid"; p.Add("rid", registerId.Value); }
    if (!string.IsNullOrEmpty(oib)) { sql += @" AND c.""OIB"" = @oib"; p.Add("oib", oib); }
    if (!string.IsNullOrEmpty(from)) { sql += @" AND o.""CompletedAt"" >= @from"; p.Add("from", from); }
    if (!string.IsNullOrEmpty(to)) { sql += @" AND o.""CompletedAt"" <= @to"; p.Add("to", to + "T23:59:59"); }
    sql += @" ORDER BY c.""OIB"", o.""ReceiptNumber"" DESC LIMIT 500";
    return Results.Json(conn.Query(sql, p));
});

// ==================== DASHBOARD STATS ====================

app.MapGet("/api/dashboard/stats", (HttpContext ctx) =>
{
    var userId = GetUserId(ctx);
    if (userId == null) return Results.Unauthorized();
    using var conn = new NpgsqlConnection(connStr);
    var isAdm = IsAdmin(ctx);
    var yearStart = $"{DateTime.Now.Year}-01-01";

    int activeRegisters, totalOrders, nonFiscalized, expiringRegistrations;
    if (isAdm)
    {
        activeRegisters = conn.QueryFirstOrDefault<int>("SELECT COUNT(*) FROM \"CashRegisters\" WHERE \"IsActive\" = true");
        totalOrders = conn.QueryFirstOrDefault<int>("SELECT COUNT(*) FROM \"Orders\" WHERE \"CompletedAt\" >= @ys", new { ys = yearStart });
        nonFiscalized = conn.QueryFirstOrDefault<int>("SELECT COUNT(*) FROM \"Orders\" WHERE \"IsFiscalized\" = false AND \"CompletedAt\" >= @ys", new { ys = yearStart });
        expiringRegistrations = conn.QueryFirstOrDefault<int>(
            "SELECT COUNT(*) FROM \"CashRegisters\" WHERE \"RegistrationDate\" != '' AND \"RegistrationDate\" IS NOT NULL AND \"RegistrationDate\"::date <= CURRENT_DATE + INTERVAL '30 days'");
    }
    else
    {
        activeRegisters = conn.QueryFirstOrDefault<int>(@"SELECT COUNT(*) FROM ""CashRegisters"" cr
            JOIN ""BusinessSpaces"" bs ON cr.""BusinessSpaceId"" = bs.""Id"" JOIN ""Companies"" c ON bs.""CompanyId"" = c.""Id""
            JOIN ""UserCompanies"" uc ON c.""Id"" = uc.""CompanyId"" AND uc.""UserId"" = @uid
            WHERE cr.""IsActive"" = true", new { uid = userId.Value });
        totalOrders = conn.QueryFirstOrDefault<int>(@"SELECT COUNT(*) FROM ""Orders"" o
            JOIN ""CashRegisters"" cr ON o.""CashRegisterId"" = cr.""Id"" JOIN ""BusinessSpaces"" bs ON cr.""BusinessSpaceId"" = bs.""Id""
            JOIN ""Companies"" c ON bs.""CompanyId"" = c.""Id"" JOIN ""UserCompanies"" uc ON c.""Id"" = uc.""CompanyId"" AND uc.""UserId"" = @uid
            WHERE o.""CompletedAt"" >= @ys", new { uid = userId.Value, ys = yearStart });
        nonFiscalized = conn.QueryFirstOrDefault<int>(@"SELECT COUNT(*) FROM ""Orders"" o
            JOIN ""CashRegisters"" cr ON o.""CashRegisterId"" = cr.""Id"" JOIN ""BusinessSpaces"" bs ON cr.""BusinessSpaceId"" = bs.""Id""
            JOIN ""Companies"" c ON bs.""CompanyId"" = c.""Id"" JOIN ""UserCompanies"" uc ON c.""Id"" = uc.""CompanyId"" AND uc.""UserId"" = @uid
            WHERE o.""IsFiscalized"" = false AND o.""CompletedAt"" >= @ys", new { uid = userId.Value, ys = yearStart });
        expiringRegistrations = 0;
    }
    return Results.Json(new { activeRegisters, totalOrders, nonFiscalized, expiringRegistrations, year = DateTime.Now.Year });
});

// ==================== PODUZEĆA PREGLED ====================

app.MapGet("/api/admin/companies/overview", (HttpContext ctx) =>
{
    if (!IsAdmin(ctx)) return Results.Unauthorized();
    using var conn = new NpgsqlConnection(connStr);
    var list = conn.Query(@"SELECT c.""Id"", c.""Name"", c.""OIB"",
        bs.""Code"" as ""SpaceCode"", cr.""Code"" as ""RegisterCode"", cr.""RegistrationDate"", cr.""IsActive"",
        (SELECT MAX(o2.""CompletedAt"") FROM ""Orders"" o2 WHERE o2.""CashRegisterId"" = cr.""Id"") as ""LastOrderDate"",
        (SELECT COUNT(*) FROM ""Orders"" o3 WHERE o3.""CashRegisterId"" = cr.""Id"" AND o3.""IsFiscalized"" = false) as ""NonFiscalized""
        FROM ""Companies"" c
        JOIN ""BusinessSpaces"" bs ON c.""Id"" = bs.""CompanyId""
        JOIN ""CashRegisters"" cr ON bs.""Id"" = cr.""BusinessSpaceId""
        ORDER BY c.""OIB"", bs.""Code"", cr.""Code""");
    return Results.Json(list);
});

// ==================== PROMET IZVJEŠTAJ ====================

app.MapGet("/api/report/revenue", (HttpContext ctx, string? from, string? to, int? registerId) =>
{
    var userId = GetUserId(ctx);
    if (userId == null) return Results.Unauthorized();
    using var conn = new NpgsqlConnection(connStr);
    var isAdm = IsAdmin(ctx);

    var sql = @"SELECT c.""Name"" as ""CompanyName"", c.""OIB"", bs.""Code"" as ""SpaceCode"",
        COALESCE(SUM(o.""Total""), 0) as ""TotalRevenue"", COUNT(o.""Id"") as ""OrderCount""
        FROM ""Orders"" o
        JOIN ""CashRegisters"" cr ON o.""CashRegisterId"" = cr.""Id""
        JOIN ""BusinessSpaces"" bs ON cr.""BusinessSpaceId"" = bs.""Id""
        JOIN ""Companies"" c ON bs.""CompanyId"" = c.""Id""";
    if (!isAdm) sql += @" JOIN ""UserCompanies"" uc ON c.""Id"" = uc.""CompanyId"" AND uc.""UserId"" = @userId";
    sql += " WHERE 1=1";

    var p = new DynamicParameters();
    if (!isAdm) p.Add("userId", userId.Value);
    if (registerId.HasValue) { sql += @" AND cr.""Id"" = @rid"; p.Add("rid", registerId.Value); }
    if (!string.IsNullOrEmpty(from)) { sql += @" AND o.""CompletedAt"" >= @from"; p.Add("from", from); }
    if (!string.IsNullOrEmpty(to)) { sql += @" AND o.""CompletedAt"" <= @to"; p.Add("to", to + "T23:59:59"); }
    sql += @" GROUP BY c.""Id"", bs.""Id"", c.""Name"", c.""OIB"", bs.""Code"" ORDER BY c.""OIB"", bs.""Code""";
    return Results.Json(conn.Query(sql, p));
});

// ==================== NEFISKALIZIRANI ====================

app.MapGet("/api/admin/orders/nonfiscalized", (HttpContext ctx, string? period) =>
{
    if (!IsAdmin(ctx)) return Results.Unauthorized();
    using var conn = new NpgsqlConnection(connStr);
    var hours = period switch { "1h" => 1, "4h" => 4, "24h" => 24, "48h" => 48, _ => 48 };
    var since = DateTime.Now.AddHours(-hours).ToString("o");
    var list = conn.Query(@"SELECT c.""Name"", c.""OIB"", bs.""Code"" as ""SpaceCode"",
        COUNT(o.""Id"") as ""NonFiscalizedCount""
        FROM ""Orders"" o
        JOIN ""CashRegisters"" cr ON o.""CashRegisterId"" = cr.""Id""
        JOIN ""BusinessSpaces"" bs ON cr.""BusinessSpaceId"" = bs.""Id""
        JOIN ""Companies"" c ON bs.""CompanyId"" = c.""Id""
        WHERE o.""IsFiscalized"" = false AND o.""CompletedAt"" >= @since
        GROUP BY c.""Id"", bs.""Id"", c.""Name"", c.""OIB"", bs.""Code"" ORDER BY c.""OIB""", new { since });
    return Results.Json(list);
});

app.MapGet("/api/admin/registrations/expiring", (HttpContext ctx) =>
{
    if (!IsAdmin(ctx)) return Results.Unauthorized();
    using var conn = new NpgsqlConnection(connStr);
    var list = conn.Query(@"SELECT c.""Name"", c.""OIB"", bs.""Code"" as ""SpaceCode"", cr.""Code"" as ""RegisterCode"",
        cr.""RegistrationDate""
        FROM ""CashRegisters"" cr
        JOIN ""BusinessSpaces"" bs ON cr.""BusinessSpaceId"" = bs.""Id""
        JOIN ""Companies"" c ON bs.""CompanyId"" = c.""Id""
        WHERE cr.""RegistrationDate"" IS NOT NULL AND cr.""RegistrationDate"" != ''
        ORDER BY cr.""RegistrationDate"" ASC");
    return Results.Json(list);
});

// ==================== DEBUG & FALLBACK ====================

app.MapGet("/api/debug/check", () =>
{
    using var conn = new NpgsqlConnection(connStr);
    var companies = conn.Query("SELECT \"Id\", \"Name\", \"OIB\", \"Address\", \"PostalCode\", \"City\", \"IBAN\", \"TaxModel\" FROM \"Companies\"");
    var lastOrders = conn.Query("SELECT \"Id\", \"ReceiptNumber\", \"CustomerName\", \"CustomerOib\", \"CustomerAddress\", \"CustomerCity\" FROM \"Orders\" ORDER BY \"Id\" DESC LIMIT 5");
    return Results.Json(new { companies, lastOrders });
});

app.MapFallback(async ctx =>
{
    var indexPath = Path.Combine(app.Environment.WebRootPath ?? "wwwroot", "index.html");
    if (File.Exists(indexPath))
    {
        ctx.Response.ContentType = "text/html";
        await ctx.Response.SendFileAsync(indexPath);
    }
    else
    {
        ctx.Response.StatusCode = 404;
        await ctx.Response.WriteAsync("ProgramCloud - web panel nije pronađen");
    }
});

app.Urls.Add("http://0.0.0.0:5100");
Console.WriteLine("ProgramCloud pokrenut na http://localhost:5100");
app.Run();

// ==================== INIT DB ====================

void InitDatabase(string cs)
{
    using var conn = new NpgsqlConnection(cs);
    conn.Open();
    conn.Execute(@"CREATE TABLE IF NOT EXISTS ""Users"" (
        ""Id"" SERIAL PRIMARY KEY, ""Username"" TEXT NOT NULL UNIQUE,
        ""PasswordHash"" TEXT NOT NULL, ""DisplayName"" TEXT, ""IsAdmin"" BOOLEAN NOT NULL DEFAULT false,
        ""Token"" TEXT, ""CreatedAt"" TEXT)");
    conn.Execute(@"CREATE TABLE IF NOT EXISTS ""Companies"" (
        ""Id"" SERIAL PRIMARY KEY, ""OIB"" TEXT NOT NULL UNIQUE,
        ""Name"" TEXT NOT NULL, ""Address"" TEXT, ""PostalCode"" TEXT, ""City"" TEXT, ""IBAN"" TEXT, ""TaxModel"" TEXT, ""CreatedAt"" TEXT)");
    conn.Execute(@"CREATE TABLE IF NOT EXISTS ""UserCompanies"" (
        ""Id"" SERIAL PRIMARY KEY, ""UserId"" INTEGER NOT NULL, ""CompanyId"" INTEGER NOT NULL)");
    conn.Execute(@"CREATE TABLE IF NOT EXISTS ""BusinessSpaces"" (
        ""Id"" SERIAL PRIMARY KEY, ""CompanyId"" INTEGER NOT NULL,
        ""Code"" TEXT NOT NULL, ""Name"" TEXT, ""IsActive"" BOOLEAN NOT NULL DEFAULT true, ""CreatedAt"" TEXT)");
    conn.Execute(@"CREATE TABLE IF NOT EXISTS ""CashRegisters"" (
        ""Id"" SERIAL PRIMARY KEY, ""BusinessSpaceId"" INTEGER NOT NULL,
        ""Code"" TEXT NOT NULL, ""LicenseKey"" TEXT NOT NULL UNIQUE,
        ""IsActive"" BOOLEAN NOT NULL DEFAULT true, ""RegistrationDate"" TEXT, ""CreatedAt"" TEXT)");
    conn.Execute(@"CREATE TABLE IF NOT EXISTS ""Orders"" (
        ""Id"" SERIAL PRIMARY KEY, ""CashRegisterId"" INTEGER NOT NULL,
        ""LocalOrderId"" INTEGER, ""ReceiptNumber"" INTEGER,
        ""Total"" REAL, ""PaymentMethod"" TEXT, ""UserName"" TEXT, ""CustomerName"" TEXT, ""CustomerOib"" TEXT,
        ""CustomerAddress"" TEXT, ""CustomerCity"" TEXT,
        ""Status"" TEXT, ""CreatedAt"" TEXT, ""CompletedAt"" TEXT, ""ItemsJson"" TEXT,
        ""TipAmount"" REAL DEFAULT 0, ""IsFiscalized"" BOOLEAN NOT NULL DEFAULT false,
        ""JIR"" TEXT, ""ZKI"" TEXT, ""FiscalizedAt"" TEXT)");
    conn.Execute(@"CREATE TABLE IF NOT EXISTS ""DailyClosings"" (
        ""Id"" SERIAL PRIMARY KEY, ""CashRegisterId"" INTEGER,
        ""ClosingNumber"" INTEGER, ""ClosedAt"" TEXT, ""TotalRevenue"" REAL,
        ""CashTotal"" REAL, ""CardTotal"" REAL, ""OtherTotal"" REAL, ""ReceiptCount"" INTEGER)");
    conn.Execute(@"CREATE TABLE IF NOT EXISTS ""AppVersions"" (
        ""Id"" SERIAL PRIMARY KEY, ""Version"" TEXT NOT NULL,
        ""DownloadUrl"" TEXT, ""Changelog"" TEXT, ""CreatedAt"" TEXT)");

    // Kreiraj admin korisnika ako ne postoji
    var adminExists = conn.QueryFirstOrDefault<int>("SELECT COUNT(*) FROM \"Users\" WHERE \"IsAdmin\" = true");
    if (adminExists == 0)
    {
        using var sha = SHA256.Create();
        var hash = Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes("0913335556")));
        conn.Execute(@"INSERT INTO ""Users"" (""Username"", ""PasswordHash"", ""DisplayName"", ""IsAdmin"", ""CreatedAt"")
            VALUES (@u, @h, 'Administrator', true, @now)",
            new { u = "tmisura2@gmail.com", h = hash, now = DateTime.Now.ToString("o") });
        Console.WriteLine("Kreiran admin korisnik");
    }
}

// ==================== MODELI ====================

record LoginRequest(string? Username, string? Password);
record UserData(string? Username, string? Password, string? DisplayName, bool IsAdmin);
record CompanyData(string? OIB, string? Name, string? Address, string? City);
record SpaceData(string? Code, string? Name);
record RegisterData(string? Code, string? RegistrationDate);
record SyncOrderData(int LocalOrderId, int ReceiptNumber, decimal Total, string? PaymentMethod,
    string? UserName, string? CustomerName, string? CustomerOib, string? CustomerAddress, string? CustomerCity,
    string? Status, string? CreatedAt, string? CompletedAt,
    decimal TipAmount, int IsFiscalized, string? JIR, string? ZKI, string? FiscalizedAt, List<SyncOrderItem>? Items);
record SyncOrderItem(string Name, decimal Qty, decimal UnitPrice, decimal LineTotal, decimal Discount, decimal VatRate, decimal VatAmount, decimal PnpRate, decimal PnpAmount, decimal DiscountPercent = 0, decimal OriginalPrice = 0, string Category = "Ostalo", string Group = "Ostalo");
record SyncOrderRequest(string CompanyOIB, string? CompanyName, string? CompanyAddress, string? CompanyCity,
    string? CompanyPostalCode, string? CompanyIBAN, string? CompanyTaxModel,
    string? BusinessSpaceCode, string? CashRegisterCode, SyncOrderData? Order);
record VersionData(string Version, string DownloadUrl, string Changelog);
record FiscalizeUpdateRequest(string CompanyOIB, string? BusinessSpaceCode, string? CashRegisterCode,
    int LocalOrderId, int ReceiptNumber, string? JIR, string? ZKI, string? FiscalizedAt);
