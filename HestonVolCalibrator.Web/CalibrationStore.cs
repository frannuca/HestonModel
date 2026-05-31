using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace HestonVolCalibrator.Web;

// ── Domain records ────────────────────────────────────────────────────────────

public record SwaptionSurfaceRow(
    long Id,
    string AsOf,
    string Source,
    string CreatedAt,
    int NExpiries,
    int NTenors,
    int NCells);

public record SabrCalibrationRow(
    long Id,
    long SwaptionSurfaceId,
    string AsOf,
    double OptionExpiry,
    double SwapTenor,
    double Forward,
    double Alpha,
    double Beta,
    double Rho,
    double Nu,
    double Shift,
    double FinalRmse,
    bool Converged,
    string Convention,
    string CreatedAt);

public record HestonSurfaceRow(
    long Id,
    string Ticker,
    string AsOf,
    double Spot,
    string Source,
    string CreatedAt,
    int NExpiries,
    int NStrikes);

public record HestonCalibrationRow(
    long Id,
    long HestonSurfaceId,
    string Ticker,
    string AsOf,
    double Kappa,
    double Theta,
    double Sigma,
    double Rho,
    double V0,
    double FinalRmse,
    string CreatedAt);

// ── Repository interface ──────────────────────────────────────────────────────

public interface ICalibrationStore
{
    // Swaption surfaces
    long SaveSwaptionSurface(SwaptionSurfaceResponse resp);
    IReadOnlyList<SwaptionSurfaceRow> ListSwaptionSurfaces();
    (SwaptionSurfaceResponse Data, string RawJson) LoadSwaptionSurface(long id);

    // SABR calibrations
    void SaveSabrCalibrations(long swaptionSurfaceId, SabrSurfaceCalibrationDbEntry[] entries);
    IReadOnlyList<SabrCalibrationRow> ListSabrCalibrations(long swaptionSurfaceId);

    // Heston surfaces
    long SaveHestonSurface(SurfaceResponse resp);
    IReadOnlyList<HestonSurfaceRow> ListHestonSurfaces();
    (SurfaceResponse Data, string RawJson) LoadHestonSurface(long id);

    // Heston calibrations
    void SaveHestonCalibration(long hestonSurfaceId, HestonCalibrationDbEntry entry);
    IReadOnlyList<HestonCalibrationRow> ListHestonCalibrations();
}

// Helper DTOs for saving calibration results.
public record SabrSurfaceCalibrationDbEntry(
    double OptionExpiry,
    double SwapTenor,
    double Forward,
    double Alpha,
    double Beta,
    double Rho,
    double Nu,
    double Shift,
    double FinalRmse,
    bool Converged,
    string Convention);

public record HestonCalibrationDbEntry(
    string Ticker,
    string AsOf,
    double Kappa,
    double Theta,
    double Sigma,
    double Rho,
    double V0,
    double FinalRmse);

// ── SQLite implementation ─────────────────────────────────────────────────────

public sealed class SqliteCalibrationStore : ICalibrationStore
{
    private readonly string _connectionString;

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public SqliteCalibrationStore(string dbPath)
    {
        _connectionString = $"Data Source={dbPath}";
        InitSchema();
    }

    private void InitSchema()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS swaption_surfaces (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                as_of       TEXT    NOT NULL,
                source      TEXT    NOT NULL,
                created_at  TEXT    NOT NULL,
                n_expiries  INTEGER NOT NULL,
                n_tenors    INTEGER NOT NULL,
                n_cells     INTEGER NOT NULL,
                data_json   TEXT    NOT NULL
            );
            CREATE TABLE IF NOT EXISTS sabr_calibrations (
                id                   INTEGER PRIMARY KEY AUTOINCREMENT,
                swaption_surface_id  INTEGER NOT NULL REFERENCES swaption_surfaces(id),
                as_of                TEXT    NOT NULL,
                option_expiry        REAL    NOT NULL,
                swap_tenor           REAL    NOT NULL,
                forward              REAL    NOT NULL,
                alpha                REAL    NOT NULL,
                beta                 REAL    NOT NULL,
                rho                  REAL    NOT NULL,
                nu                   REAL    NOT NULL,
                shift                REAL    NOT NULL,
                final_rmse           REAL    NOT NULL,
                converged            INTEGER NOT NULL,
                convention           TEXT    NOT NULL,
                created_at           TEXT    NOT NULL
            );
            CREATE TABLE IF NOT EXISTS heston_surfaces (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                ticker      TEXT    NOT NULL,
                as_of       TEXT    NOT NULL,
                spot        REAL    NOT NULL,
                source      TEXT    NOT NULL,
                created_at  TEXT    NOT NULL,
                n_expiries  INTEGER NOT NULL,
                n_strikes   INTEGER NOT NULL,
                data_json   TEXT    NOT NULL
            );
            CREATE TABLE IF NOT EXISTS heston_calibrations (
                id                 INTEGER PRIMARY KEY AUTOINCREMENT,
                heston_surface_id  INTEGER NOT NULL REFERENCES heston_surfaces(id),
                ticker             TEXT    NOT NULL,
                as_of              TEXT    NOT NULL,
                kappa              REAL    NOT NULL,
                theta              REAL    NOT NULL,
                sigma              REAL    NOT NULL,
                rho                REAL    NOT NULL,
                v0                 REAL    NOT NULL,
                final_rmse         REAL    NOT NULL,
                created_at         TEXT    NOT NULL
            );";
        cmd.ExecuteNonQuery();
    }

    // ── Swaption surfaces ─────────────────────────────────────────────────────

    public long SaveSwaptionSurface(SwaptionSurfaceResponse resp)
    {
        var expiries = new HashSet<double>();
        var tenors   = new HashSet<double>();
        foreach (var pt in resp.VolSurface)
        {
            expiries.Add(pt.OptionExpiry);
            tenors.Add(pt.SwapTenor);
        }

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO swaption_surfaces (as_of, source, created_at, n_expiries, n_tenors, n_cells, data_json)
            VALUES ($asOf, $source, $createdAt, $nE, $nT, $nC, $json);
            SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$asOf",      resp.AsOf);
        cmd.Parameters.AddWithValue("$source",    resp.Source);
        cmd.Parameters.AddWithValue("$createdAt", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$nE",        expiries.Count);
        cmd.Parameters.AddWithValue("$nT",        tenors.Count);
        cmd.Parameters.AddWithValue("$nC",        resp.VolSurface.Length);
        cmd.Parameters.AddWithValue("$json",      JsonSerializer.Serialize(resp, _json));
        return (long)cmd.ExecuteScalar()!;
    }

    public IReadOnlyList<SwaptionSurfaceRow> ListSwaptionSurfaces()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT id, as_of, source, created_at, n_expiries, n_tenors, n_cells " +
            "FROM swaption_surfaces ORDER BY id DESC;";
        using var rdr = cmd.ExecuteReader();
        var rows = new List<SwaptionSurfaceRow>();
        while (rdr.Read())
            rows.Add(new(rdr.GetInt64(0), rdr.GetString(1), rdr.GetString(2),
                         rdr.GetString(3), rdr.GetInt32(4), rdr.GetInt32(5), rdr.GetInt32(6)));
        return rows;
    }

    public (SwaptionSurfaceResponse Data, string RawJson) LoadSwaptionSurface(long id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT data_json FROM swaption_surfaces WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        var json = (string?)cmd.ExecuteScalar()
            ?? throw new KeyNotFoundException($"No swaption surface with id={id}");
        return (JsonSerializer.Deserialize<SwaptionSurfaceResponse>(json, _json)!, json);
    }

    // ── SABR calibrations ─────────────────────────────────────────────────────

    public void SaveSabrCalibrations(long swaptionSurfaceId, SabrSurfaceCalibrationDbEntry[] entries)
    {
        string asOf = DateTime.UtcNow.ToString("o");
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        foreach (var e in entries)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                INSERT INTO sabr_calibrations
                    (swaption_surface_id, as_of, option_expiry, swap_tenor, forward,
                     alpha, beta, rho, nu, shift, final_rmse, converged, convention, created_at)
                VALUES ($sid, $asOf, $oe, $st, $fwd,
                        $alpha, $beta, $rho, $nu, $shift, $rmse, $conv, $convention, $createdAt);";
            cmd.Parameters.AddWithValue("$sid",        swaptionSurfaceId);
            cmd.Parameters.AddWithValue("$asOf",       asOf);
            cmd.Parameters.AddWithValue("$oe",         e.OptionExpiry);
            cmd.Parameters.AddWithValue("$st",         e.SwapTenor);
            cmd.Parameters.AddWithValue("$fwd",        e.Forward);
            cmd.Parameters.AddWithValue("$alpha",      e.Alpha);
            cmd.Parameters.AddWithValue("$beta",       e.Beta);
            cmd.Parameters.AddWithValue("$rho",        e.Rho);
            cmd.Parameters.AddWithValue("$nu",         e.Nu);
            cmd.Parameters.AddWithValue("$shift",      e.Shift);
            cmd.Parameters.AddWithValue("$rmse",       e.FinalRmse);
            cmd.Parameters.AddWithValue("$conv",       e.Converged ? 1 : 0);
            cmd.Parameters.AddWithValue("$convention", e.Convention);
            cmd.Parameters.AddWithValue("$createdAt",  asOf);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public IReadOnlyList<SabrCalibrationRow> ListSabrCalibrations(long swaptionSurfaceId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, swaption_surface_id, as_of, option_expiry, swap_tenor, forward,
                   alpha, beta, rho, nu, shift, final_rmse, converged, convention, created_at
            FROM sabr_calibrations WHERE swaption_surface_id = $sid
            ORDER BY option_expiry, swap_tenor;";
        cmd.Parameters.AddWithValue("$sid", swaptionSurfaceId);
        using var rdr = cmd.ExecuteReader();
        var rows = new List<SabrCalibrationRow>();
        while (rdr.Read())
            rows.Add(new(
                rdr.GetInt64(0), rdr.GetInt64(1), rdr.GetString(2),
                rdr.GetDouble(3), rdr.GetDouble(4), rdr.GetDouble(5),
                rdr.GetDouble(6), rdr.GetDouble(7), rdr.GetDouble(8),
                rdr.GetDouble(9), rdr.GetDouble(10), rdr.GetDouble(11),
                rdr.GetInt32(12) != 0, rdr.GetString(13), rdr.GetString(14)));
        return rows;
    }

    // ── Heston surfaces ───────────────────────────────────────────────────────

    public long SaveHestonSurface(SurfaceResponse resp)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO heston_surfaces
                (ticker, as_of, spot, source, created_at, n_expiries, n_strikes, data_json)
            VALUES ($ticker, $asOf, $spot, $source, $createdAt, $nE, $nS, $json);
            SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$ticker",    resp.Ticker);
        cmd.Parameters.AddWithValue("$asOf",      DateTime.UtcNow.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("$spot",      resp.Spot);
        cmd.Parameters.AddWithValue("$source",    resp.Source);
        cmd.Parameters.AddWithValue("$createdAt", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$nE",        resp.Expiries.Length);
        cmd.Parameters.AddWithValue("$nS",        resp.Strikes.Length);
        cmd.Parameters.AddWithValue("$json",      JsonSerializer.Serialize(resp, _json));
        return (long)cmd.ExecuteScalar()!;
    }

    public IReadOnlyList<HestonSurfaceRow> ListHestonSurfaces()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT id, ticker, as_of, spot, source, created_at, n_expiries, n_strikes " +
            "FROM heston_surfaces ORDER BY id DESC;";
        using var rdr = cmd.ExecuteReader();
        var rows = new List<HestonSurfaceRow>();
        while (rdr.Read())
            rows.Add(new(rdr.GetInt64(0), rdr.GetString(1), rdr.GetString(2),
                         rdr.GetDouble(3), rdr.GetString(4), rdr.GetString(5),
                         rdr.GetInt32(6), rdr.GetInt32(7)));
        return rows;
    }

    public (SurfaceResponse Data, string RawJson) LoadHestonSurface(long id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT data_json FROM heston_surfaces WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        var json = (string?)cmd.ExecuteScalar()
            ?? throw new KeyNotFoundException($"No Heston surface with id={id}");
        return (JsonSerializer.Deserialize<SurfaceResponse>(json, _json)!, json);
    }

    // ── Heston calibrations ───────────────────────────────────────────────────

    public void SaveHestonCalibration(long hestonSurfaceId, HestonCalibrationDbEntry e)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO heston_calibrations
                (heston_surface_id, ticker, as_of, kappa, theta, sigma, rho, v0, final_rmse, created_at)
            VALUES ($sid, $ticker, $asOf, $kappa, $theta, $sigma, $rho, $v0, $rmse, $createdAt);";
        cmd.Parameters.AddWithValue("$sid",       hestonSurfaceId);
        cmd.Parameters.AddWithValue("$ticker",    e.Ticker);
        cmd.Parameters.AddWithValue("$asOf",      e.AsOf);
        cmd.Parameters.AddWithValue("$kappa",     e.Kappa);
        cmd.Parameters.AddWithValue("$theta",     e.Theta);
        cmd.Parameters.AddWithValue("$sigma",     e.Sigma);
        cmd.Parameters.AddWithValue("$rho",       e.Rho);
        cmd.Parameters.AddWithValue("$v0",        e.V0);
        cmd.Parameters.AddWithValue("$rmse",      e.FinalRmse);
        cmd.Parameters.AddWithValue("$createdAt", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<HestonCalibrationRow> ListHestonCalibrations()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT hc.id, hc.heston_surface_id, hc.ticker, hc.as_of,
                   hc.kappa, hc.theta, hc.sigma, hc.rho, hc.v0, hc.final_rmse, hc.created_at
            FROM heston_calibrations hc
            ORDER BY hc.id DESC;";
        using var rdr = cmd.ExecuteReader();
        var rows = new List<HestonCalibrationRow>();
        while (rdr.Read())
            rows.Add(new(
                rdr.GetInt64(0), rdr.GetInt64(1), rdr.GetString(2), rdr.GetString(3),
                rdr.GetDouble(4), rdr.GetDouble(5), rdr.GetDouble(6),
                rdr.GetDouble(7), rdr.GetDouble(8), rdr.GetDouble(9),
                rdr.GetString(10)));
        return rows;
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }
}
