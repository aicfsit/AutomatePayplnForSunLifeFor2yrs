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
                "    UpdatedOn DATETIME NOT NULL DEFAULT GETDATE() " +
                "  ) " +
                "END";

            using (SqlConnection conn = new SqlConnection(_connectionString))
            {
                conn.Open();
                using (SqlCommand cmd = new SqlCommand(sql, conn))
                {
                    cmd.ExecuteNonQuery();
                }
            }
        }

        // Read the active portal credentials for the given app key.
        public PortalCredential GetPortalCredential(string appKey)
        {
            string sql =
                "SELECT TOP 1 Username, [Password] FROM dbo.AutomationCredentials " +
                "WHERE AppKey = @key AND IsActive = 1 ORDER BY UpdatedOn DESC";

            using (SqlConnection conn = new SqlConnection(_connectionString))
            {
                conn.Open();
                using (SqlCommand cmd = new SqlCommand(sql, conn))
                {
                    cmd.Parameters.Add("@key", SqlDbType.VarChar, 50).Value = appKey;
                    using (SqlDataReader reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            PortalCredential c = new PortalCredential();
                            c.Username = reader.IsDBNull(0) ? null : reader.GetString(0);
                            c.Password = reader.IsDBNull(1) ? null : reader.GetString(1);
                            return c;
                        }
                    }
                }
            }
            return null;
        }

        // Make sure the tracking table exists before we start.
        public void EnsureLogTable()
        {
            string sql =
                "IF OBJECT_ID('dbo.PremiumExtractionLog','U') IS NULL " +
                "BEGIN " +
                "  CREATE TABLE dbo.PremiumExtractionLog ( " +
                "    Id INT IDENTITY(1,1) PRIMARY KEY, " +
                "    polrefno VARCHAR(50) NULL, " +
                "    polcod VARCHAR(50) NULL, " +
                "    Amount MONEY NULL, " +
                "    Status VARCHAR(20) NULL, " +
                "    Message VARCHAR(500) NULL, " +
                "    [Date] DATETIME NOT NULL DEFAULT GETDATE() " +
                "  ) " +
                "END";

            using (SqlConnection conn = new SqlConnection(_connectionString))
            {
                conn.Open();
                using (SqlCommand cmd = new SqlCommand(sql, conn))
                {
                    cmd.ExecuteNonQuery();
                }
            }
        }

        // Run the stored procedure and pull the policy list from its result set.
        public List<PolicyItem> GetPoliciesFromStoredProc()
        {
            List<PolicyItem> list = new List<PolicyItem>();

            using (SqlConnection conn = new SqlConnection(_connectionString))
            {
                conn.Open();
                using (SqlCommand cmd = new SqlCommand(
                    "SP_PREMIUM_DUE_NOTIFICATION_JUVO_NEW_FOR_SECONDYRPAYPLN", conn))
                {
                    cmd.CommandType = CommandType.StoredProcedure;
                    cmd.CommandTimeout = 300;

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
                "(polrefno, polcod, Amount, Status, Message, [Date]) " +
                "VALUES (@ref, @cod, @amt, @status, @msg, GETDATE())";

            using (SqlConnection conn = new SqlConnection(_connectionString))
            {
                conn.Open();
                using (SqlCommand cmd = new SqlCommand(sql, conn))
                {
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
