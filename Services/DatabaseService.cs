using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using AutomatePayplnForSunLifeFor2yrs.Models;

namespace AutomatePayplnForSunLifeFor2yrs.Services
{
    public class DatabaseService
    {
        private readonly string _connectionString;

        public DatabaseService(string connectionString)
        {
            _connectionString = connectionString;
        }

        // Make sure the credentials table exists before we read it.
        public void EnsureCredentialTable()
        {
            string sql =
                "IF OBJECT_ID('dbo.AutomationCredentials','U') IS NULL " +
                "BEGIN " +
                "  CREATE TABLE dbo.AutomationCredentials ( " +
                "    Id INT IDENTITY(1,1) PRIMARY KEY, " +
                "    AppKey VARCHAR(50) NOT NULL, " +
                "    Username VARCHAR(200) NOT NULL, " +
                "    [Password] VARCHAR(200) NOT NULL, " +
                "    IsActive BIT NOT NULL DEFAULT 1, " +
                "    UpdatedOn DATETIME NOT NULL DEFAULT GETDATE(), " +
                "    entity VARCHAR(50) NULL, " +
                "    StoredProcedure VARCHAR(200) NULL " +
                "  ) " +
                "END " +
                // Existing installs predate these columns.
                "IF COL_LENGTH('dbo.AutomationCredentials','entity') IS NULL " +
                "  ALTER TABLE dbo.AutomationCredentials ADD entity VARCHAR(50) NULL; " +
                "IF COL_LENGTH('dbo.AutomationCredentials','StoredProcedure') IS NULL " +
                "  ALTER TABLE dbo.AutomationCredentials " +
                "    ADD StoredProcedure VARCHAR(200) NULL; " +
                "IF COL_LENGTH('dbo.AutomationCredentials','ACTCOD') IS NULL " +
                "  ALTER TABLE dbo.AutomationCredentials ADD ACTCOD VARCHAR(50) NULL;";

            using (SqlConnection conn = new SqlConnection(_connectionString))
            {
                conn.Open();
                using (SqlCommand cmd = new SqlCommand(sql, conn))
                {
                    cmd.ExecuteNonQuery();
                }
            }
        }

        // Every active row is one entity to process. This table is the single
        // source of truth for what runs: add a row to add an entity, set
        // IsActive = 0 to stop one, with no code or config change.
        public List<EntityRegistration> GetActiveEntities()
        {
            List<EntityRegistration> list = new List<EntityRegistration>();

            string sql =
                "SELECT Id, AppKey, Username, [Password], entity, " +
                "       StoredProcedure, ACTCOD " +
                "FROM dbo.AutomationCredentials " +
                "WHERE IsActive = 1 ORDER BY Id";

            using (SqlConnection conn = new SqlConnection(_connectionString))
            {
                conn.Open();
                using (SqlCommand cmd = new SqlCommand(sql, conn))
                {
                    using (SqlDataReader reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            EntityRegistration e = new EntityRegistration();
                            e.Id = reader.GetInt32(0);
                            e.AppKey = ReadTrimmed(reader, 1);
                            e.Username = ReadTrimmed(reader, 2);
                            e.Password = reader.IsDBNull(3) ? null : reader.GetString(3);
                            e.Name = ReadTrimmed(reader, 4);
                            e.StoredProcedure = ReadTrimmed(reader, 5);
                            e.ActCod = ReadTrimmed(reader, 6);
                            list.Add(e);
                        }
                    }
                }
            }

            return list;
        }

        private static string ReadTrimmed(SqlDataReader reader, int ordinal)
        {
            return reader.IsDBNull(ordinal)
                ? null : reader.GetValue(ordinal).ToString().Trim();
        }

        // True if the stored procedure exists. Checked before the browser
        // starts so a bad name in config fails fast instead of after a login.
        public bool ProcedureExists(string procedureName)
        {
            using (SqlConnection conn = new SqlConnection(_connectionString))
            {
                conn.Open();
                using (SqlCommand cmd = new SqlCommand(
                    "SELECT CASE WHEN OBJECT_ID(@name,'P') IS NULL THEN 0 ELSE 1 END",
                    conn))
                {
                    cmd.Parameters.Add("@name", SqlDbType.NVarChar, 260).Value =
                        procedureName;
                    return Convert.ToInt32(cmd.ExecuteScalar()) == 1;
                }
            }
        }

        // Make sure the tracking table exists before we start.
        public void EnsureLogTable()
        {
            string sql =
                "IF OBJECT_ID('dbo.PremiumExtractionLog','U') IS NULL " +
                "BEGIN " +
                "  CREATE TABLE dbo.PremiumExtractionLog ( " +
                "    Id INT IDENTITY(1,1) PRIMARY KEY, " +
                "    Entity VARCHAR(50) NULL, " +
                "    polrefno VARCHAR(50) NULL, " +
                "    polcod VARCHAR(50) NULL, " +
                "    Amount MONEY NULL, " +
                "    Status VARCHAR(20) NULL, " +
                "    Message VARCHAR(500) NULL, " +
                "    [Date] DATETIME NOT NULL DEFAULT GETDATE() " +
                "  ) " +
                "END " +
                // Existing installs predate the Entity column.
                "IF COL_LENGTH('dbo.PremiumExtractionLog','Entity') IS NULL " +
                "  ALTER TABLE dbo.PremiumExtractionLog ADD Entity VARCHAR(50) NULL";

            using (SqlConnection conn = new SqlConnection(_connectionString))
            {
                conn.Open();
                using (SqlCommand cmd = new SqlCommand(sql, conn))
                {
                    cmd.ExecuteNonQuery();
                }
            }
        }

        // True if the procedure declares an ACTCOD parameter. Entities share one
        // procedure, so a missing ACTCOD would silently give every entity the
        // same policy list; the caller checks this before running anything.
        public bool ProcedureHasActCodParameter(string procedureName)
        {
            return FindActCodParameter(procedureName) != null;
        }

        // Look up the procedure's own ACTCOD parameter so its real name and
        // declared type are used, instead of assuming "@ACTCOD" and a type.
        // Returns null if the procedure has no such parameter.
        private SqlParameter FindActCodParameter(string procedureName)
        {
            if (string.IsNullOrEmpty(procedureName))
            {
                return null;
            }

            using (SqlConnection conn = new SqlConnection(_connectionString))
            {
                conn.Open();
                using (SqlCommand probe = new SqlCommand(procedureName, conn))
                {
                    probe.CommandType = CommandType.StoredProcedure;
                    try
                    {
                        SqlCommandBuilder.DeriveParameters(probe);
                    }
                    catch (Exception)
                    {
                        // No permission to read the definition, or no such proc.
                        return null;
                    }

                    foreach (SqlParameter p in probe.Parameters)
                    {
                        if (p.Direction == ParameterDirection.ReturnValue ||
                            p.ParameterName == null)
                        {
                            continue;
                        }

                        if (p.ParameterName.TrimStart('@').IndexOf(
                            "actcod", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            SqlParameter found =
                                new SqlParameter(p.ParameterName, p.SqlDbType);
                            if (p.Size > 0)
                            {
                                found.Size = p.Size;
                            }
                            return found;
                        }
                    }
                }
            }

            return null;
        }

        // Run the entity's stored procedure and pull its policy list.
        // actCod selects which entity's policies the procedure returns.
        public List<PolicyItem> GetPoliciesFromStoredProc(
            string procedureName, string actCod)
        {
            if (string.IsNullOrEmpty(procedureName))
            {
                throw new ArgumentException(
                    "No stored procedure configured for this entity.");
            }

            SqlParameter actCodParam = FindActCodParameter(procedureName);

            List<PolicyItem> list = new List<PolicyItem>();

            using (SqlConnection conn = new SqlConnection(_connectionString))
            {
                conn.Open();
                using (SqlCommand cmd = new SqlCommand(procedureName, conn))
                {
                    cmd.CommandType = CommandType.StoredProcedure;
                    cmd.CommandTimeout = 300;

                    if (actCodParam != null)
                    {
                        // The parameter is scalar, and T-SQL does not expand a
                        // list inside IN (@p). "3449,3458" would match nothing.
                        if (actCod != null && actCod.IndexOf(',') >= 0)
                        {
                            Console.WriteLine("  WARNING: ACTCOD '" + actCod +
                                "' looks like a list. " + actCodParam.ParameterName +
                                " is a single value; use one row per code.");
                        }

                        // Silent truncation to the declared size would turn into
                        // an empty result set that looks like "no policies due".
                        if (actCodParam.Size > 0 && actCod != null &&
                            actCod.Length > actCodParam.Size)
                        {
                            throw new Exception("ACTCOD '" + actCod + "' is " +
                                actCod.Length + " characters but " +
                                actCodParam.ParameterName + " accepts only " +
                                actCodParam.Size + "; it would be truncated.");
                        }

                        actCodParam.Value = string.IsNullOrEmpty(actCod)
                            ? (object)DBNull.Value : actCod;
                        cmd.Parameters.Add(actCodParam);
                        Console.WriteLine("  passing " + actCodParam.ParameterName +
                            " = " + actCod);
                    }

                    using (SqlDataReader reader = cmd.ExecuteReader())
                    {
                        int ibsOrdinal = reader.GetOrdinal("IBS #");
                        int polcodOrdinal = reader.GetOrdinal("polcod");

                        while (reader.Read())
                        {
                            PolicyItem item = new PolicyItem();
                            item.PolRefNo = reader.IsDBNull(ibsOrdinal)
                                ? null : reader.GetValue(ibsOrdinal).ToString().Trim();
                            item.PolCod = reader.IsDBNull(polcodOrdinal)
                                ? null : reader.GetValue(polcodOrdinal).ToString().Trim();
                            list.Add(item);
                        }
                    }
                }
            }

            return list;
        }

        // UPDATE paypln SET clnprmamt=@amt WHERE polrefno=@ref AND planyr=2
        public int UpdatePremiumAmount(string polRefNo, decimal amount)
        {
            string sql =
                "UPDATE paypln SET clnprmamt = @amt,recdat=getdate(),recusr='system' " +
                "WHERE polrefno = @ref AND planyr = 2";

            using (SqlConnection conn = new SqlConnection(_connectionString))
            {
                conn.Open();
                using (SqlCommand cmd = new SqlCommand(sql, conn))
                {
                    cmd.Parameters.Add("@amt", SqlDbType.Money).Value = amount;
                    cmd.Parameters.Add("@ref", SqlDbType.VarChar, 50).Value = polRefNo;
                    return cmd.ExecuteNonQuery();
                }
            }
        }

        public void InsertLog(ProcessResult result)
        {
            string sql =
                "INSERT INTO dbo.PremiumExtractionLog " +
                "(Entity, polrefno, polcod, Amount, Status, Message, [Date]) " +
                "VALUES (@entity, @ref, @cod, @amt, @status, @msg, GETDATE())";

            using (SqlConnection conn = new SqlConnection(_connectionString))
            {
                conn.Open();
                using (SqlCommand cmd = new SqlCommand(sql, conn))
                {
                    cmd.Parameters.Add("@entity", SqlDbType.VarChar, 50).Value =
                        (object)result.Entity ?? DBNull.Value;
                    cmd.Parameters.Add("@ref", SqlDbType.VarChar, 50).Value =
                        (object)result.PolRefNo ?? DBNull.Value;
                    cmd.Parameters.Add("@cod", SqlDbType.VarChar, 50).Value =
                        (object)result.PolCod ?? DBNull.Value;
                    cmd.Parameters.Add("@amt", SqlDbType.Money).Value =
                        result.Amount.HasValue ? (object)result.Amount.Value : DBNull.Value;
                    cmd.Parameters.Add("@status", SqlDbType.VarChar, 20).Value =
                        (object)result.Status ?? DBNull.Value;
                    cmd.Parameters.Add("@msg", SqlDbType.VarChar, 500).Value =
                        (object)result.Message ?? DBNull.Value;
                    cmd.ExecuteNonQuery();
                }
            }
        }
    }
}
