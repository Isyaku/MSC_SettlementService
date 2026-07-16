using Jaiz_POS_MSC_SettlementService.Data;
using Jaiz_POS_MSC_SettlementService.Models;
using Microsoft.EntityFrameworkCore;
using Oracle.ManagedDataAccess.Client;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using static Jaiz_POS_MSC_SettlementService.Models.StatusModel;

namespace Jaiz_POS_MSC_SettlementService
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly IConfiguration _configuration;
        private readonly IServiceProvider _serviceProvider;

        public Worker(ILogger<Worker> logger, IConfiguration configuration, IServiceProvider serviceProvider)
        {
            _logger = logger;
            _configuration = configuration;
            _serviceProvider = serviceProvider;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("MSC Settlement Worker Started...");
            Console.WriteLine("MSC Settlement Worker Started...");

            var interval = _configuration.GetValue<int>("ServiceSettings:PollingIntervalSeconds");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ProcessSingleBatch(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unhandled service error");
                    Console.WriteLine($"{ex}, Unhandled service error");
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(interval), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            _logger.LogInformation("Settlement Service Stopped");
            Console.WriteLine("Settlement Service Stopped");
        }

        private async Task ProcessSingleBatch(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Checking for pending batch at {Time}", DateTimeOffset.Now);
            Console.WriteLine($"Checking for pending batch at Time, {DateTimeOffset.Now}");

            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var allowedBatchs = new[]
            {
                ((int)UploadStatus.Approved).ToString(),
                ((int)UploadStatus.PartialOrFailed).ToString(),
                ((int)UploadStatus.Failed).ToString(),
                ((int)UploadStatus.NoRecords).ToString(),
                ((int)UploadStatus.Error).ToString()
            };

            var batches = await db.MSC_Request_Upload.Where(r => allowedBatchs.Contains(r.Status)).OrderBy(r => r.UploadDate).ToListAsync(stoppingToken);
            if (!batches.Any())
            {
                _logger.LogInformation("No pending batch found at {Time}", DateTimeOffset.Now);
                Console.WriteLine($"No pending batch found at Time, {DateTimeOffset.Now}");
                return;
            }

            foreach (var batch in batches)
            {

                _logger.LogInformation("Processing batch ID: {ID}, UploadDate: {Date}", batch.ID, batch.UploadDate);
                Console.WriteLine($"Processing batch ID: {batch.ID}, UploadDate: {batch.UploadDate}");

                batch.Status = ((int)UploadStatus.InBatchProgress).ToString();
                await db.SaveChangesAsync(stoppingToken);

                var allowedSettlements = new[]
                {((int)SettlementStatus.New).ToString(),
                ((int)SettlementStatus.Failed).ToString()};

                var settlements = await db.MSC_Request.Where(r => r.MSC_Request_Upload_ID == batch.ID && allowedSettlements.Contains(r.Status)).ToListAsync(stoppingToken);

                bool allSuccess = true;

                if (settlements.Any())
                {
                    foreach (var settlement in settlements)
                    {
                        if (stoppingToken.IsCancellationRequested)
                        {
                            Console.WriteLine($"Job cancellation requested during settlement processing.");
                            _logger.LogInformation("Job cancellation requested during settlement processing.");
                            return;
                        }

                        try
                        {
                            settlement.Status = ((int)SettlementStatus.InProgress).ToString();
                            decimal amount = Math.Abs((decimal)settlement.CVAmount);
                            string sessionId = settlement.TransactionId;

                            if (amount == 0 || decimal.Round(amount, 2) != amount)
                            {
                                settlement.Status = ((int)SettlementStatus.InvalidAmount).ToString();

                                Console.WriteLine($"Invalid amount for Acct: {settlement.AccountNumber}, Batch: {batch.ID}, Amount: {amount}");
                                _logger.LogInformation("Invalid amount for Acct: {Acct}, Batch: {Batch}, Amount: {Amount}", settlement.AccountNumber, batch.ID, amount);

                                await db.SaveChangesAsync(stoppingToken);
                                continue;
                            }

                            if (string.IsNullOrWhiteSpace(sessionId))
                            {
                                sessionId = GetSessionID().ToString();
                                settlement.TransactionId = sessionId;
                            }

                            await db.SaveChangesAsync(stoppingToken);

                            var response = await RunSettlementJob(sessionId, amount, settlement.MSC_Request_Upload_ID.ToString(), settlement.DebitAcct, batch.TransientAccount, "TransientGL");

                            if (response == "00")
                            {
                                settlement.Status = ((int)SettlementStatus.Success).ToString();
                                Console.WriteLine($"Successful settlement for Acct: {settlement.AccountNumber}, Batch: {batch.ID}");
                                _logger.LogInformation("Successful settlement for Acct: {Acct}, Batch: {Batch}", settlement.AccountNumber, batch.ID);
                            }
                            else
                            {
                                var tranxStatus = await GetTransactionStatusAsync(sessionId);

                                if (tranxStatus == 0)
                                {
                                    settlement.Status = ((int)SettlementStatus.Success).ToString();
                                    Console.WriteLine($"Successful settlement for Acct: {settlement.AccountNumber}, Batch: {batch.ID}");
                                    _logger.LogInformation("Successful settlement for Acct: {Acct}, Batch: {Batch}", settlement.AccountNumber, batch.ID);
                                }
                                else
                                {
                                    settlement.Status = ((int)SettlementStatus.Failed).ToString();
                                    allSuccess = false;

                                    Console.WriteLine($"Settlement failed for Acct: {settlement.AccountNumber}, Batch: {batch.ID}, Response: {response}");
                                    _logger.LogWarning("Settlement failed for Acct: {Acct}, Batch: {Batch}, Response: {Response}", settlement.AccountNumber, batch.ID, response);
                                }
                            }

                            await db.SaveChangesAsync(stoppingToken);
                        }
                        catch (Exception ex)
                        {
                            settlement.Status = ((int)SettlementStatus.Failed).ToString();
                            allSuccess = false;
                            await db.SaveChangesAsync(stoppingToken);

                            Console.WriteLine($"{ex}, Exception during settlement for Acct: {settlement.AccountNumber} in batch: {batch.ID}");
                            _logger.LogError(ex, "Exception during settlement for Acct: {Acct} in batch: {Batch}", settlement.AccountNumber, batch.ID);
                        }
                    }

                    batch.Status = allSuccess
                        ? ((int)UploadStatus.BatchDebitSuccess).ToString()
                        : ((int)UploadStatus.PartialOrFailed).ToString();

                    await db.SaveChangesAsync(stoppingToken);
                }
                else
                {

                    Console.WriteLine($"No settlements found for batch ID: {batch.ID}");
                    _logger.LogWarning("No settlements found for batch ID: {BatchID}", batch.ID);
                    batch.Status = ((int)UploadStatus.NoRecords).ToString();
                    await db.SaveChangesAsync(stoppingToken);
                }

                // Final GL settlement only if all debit settlements succeeded
                if (allSuccess)
                {
                    batch.Status = ((int)UploadStatus.InProgress).ToString();
                    await db.SaveChangesAsync(stoppingToken);

                    try
                    {
                        decimal amount = (decimal)batch.TotalAmount;
                        string batchSessionId = batch.TransactionId;

                        if (string.IsNullOrWhiteSpace(batchSessionId))
                        {
                            batchSessionId = GetSessionID().ToString();
                            batch.TransactionId = batchSessionId;
                        }

                        var response = await RunSettlementJob(batchSessionId, amount, batch.ID.ToString(), batch.TransientAccount, batch.CreditAccount, "HeadGL");

                        if (response == "00")
                        {
                            batch.Status = ((int)UploadStatus.Success).ToString();

                            Console.WriteLine($"GL settlement successful for batch: {batch.ID}");
                            _logger.LogInformation("GL settlement successful for batch: {BatchID}", batch.ID);
                        }
                        else
                        {
                            var tranxStatus = await GetTransactionStatusAsync(batchSessionId);

                            if (tranxStatus == 0)
                            {
                                batch.Status = ((int)UploadStatus.Success).ToString();

                                Console.WriteLine($"GL settlement successful for batch: {batch.ID}");
                                _logger.LogInformation("GL settlement successful for batch: {BatchID}", batch.ID);
                            }
                            else
                            {
                                batch.Status = ((int)UploadStatus.Failed).ToString();

                                Console.WriteLine($"GL settlement failed for batch: {batch.ID}, Response: {response}");
                                _logger.LogWarning("GL settlement failed for batch: {BatchID}, Response: {Response}", batch.ID, response);
                            }
                        }

                        await db.SaveChangesAsync(stoppingToken);

                        //TODO
                        //SEND NOTIFICATION
                        SendNotificationEmail($"{batch.Supervisor}@jaizbankplc.com", "POS merchant charge settlement has been completed.");
                        //util.SendNotificationEmail("im04220@jaizbankplc.com", "POS merchant charge settlement has been completed.");
                    }
                    catch (Exception ex)
                    {
                        batch.Status = ((int)UploadStatus.Failed).ToString();
                        await db.SaveChangesAsync(stoppingToken);
                        Console.WriteLine($"{ex}, Exception during GL settlement for batch: {batch.ID}");
                        _logger.LogError(ex, "Exception during GL settlement for batch: {BatchID}", batch.ID);
                    }
                }

                _logger.LogInformation("Finished processing batch ID: {BatchID} at {Time}", batch.ID, DateTimeOffset.Now);
                Console.WriteLine($"Finished processing batch ID: {batch.ID} at {DateTimeOffset.Now}");
            }
        }

        public string SendNotificationEmail(string emailaddress, string message)
        {
            //string templatePath = $"{Directory.GetCurrentDirectory()}\\EmailTemplate\\NotificationMail.htm";
            string templatePath = Path.Combine(AppContext.BaseDirectory, "EmailTemplate", "NotificationMail.htm");

            string strMessage = File.ReadAllText(templatePath);
            string mailBody = string.Empty;
            mailBody = strMessage;
            mailBody = mailBody.Replace("#Message#", message);
            try
            {
                JaizEmailService.JaizHelperClient service = new JaizEmailService.JaizHelperClient();
                JaizEmailService.EmailObject obj = new JaizEmailService.EmailObject
                {
                    Attachment = null,
                    EmailAddress = emailaddress,
                    EmailContent = mailBody,
                    FromAddress = "platform@jaizbankplc.com",
                    HasAttachment = 0,
                    SenderId = "SRVMGT",
                    Subject = "POS_MSC Notification"
                };

                return service.SendEmailViaHelper(obj).ToString();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{ex}, Exception sending email notification");
                _logger.LogError($"{ex}, Exception sending email notification");

                return null;
            }
        }

        public string GetSessionID()
        {
            string result = "";

            try
            {
                JaizInternalService.processmessageSoapClient client = new JaizInternalService.processmessageSoapClient(0);
                string res = client.getSessionID();
                result = "000006" + res;
                //return "000006";
            }

            catch (Exception ex)
            {
                Console.WriteLine($"Error Getting session Id: {ex.Message}");
                _logger.LogError($"Error Getting session Id: {ex.Message}");

            }
            return result;
        }

        public async Task<int> GetTransactionStatusAsync(string sessionId)
        {
            try
            {
                var connectionString = _configuration.GetConnectionString("OracleDb");
                var schema = _configuration["DatabaseSettings:Schema"];

                var procedureName = string.IsNullOrWhiteSpace(schema)
                    ? "P_GET_TRX_STATUS"
                    : $"{schema}.P_GET_TRX_STATUS";

                await using var conn = new OracleConnection(connectionString);
                await using var cmd = new OracleCommand(procedureName, conn)
                {
                    CommandType = CommandType.StoredProcedure,
                    BindByName = true
                };

                cmd.Parameters.Add("I_SessionID", OracleDbType.NVarchar2).Value = sessionId;

                cmd.Parameters.Add("O_RESP_CODE", OracleDbType.Int32)
                              .Direction = ParameterDirection.Output;

                await conn.OpenAsync();
                await cmd.ExecuteNonQueryAsync();

                var value = cmd.Parameters["O_RESP_CODE"]?.Value;

                if (value is Oracle.ManagedDataAccess.Types.OracleDecimal dec)
                {
                    var result = dec.ToInt32();
                    return result == 0 ? 0 : 1; // success = 0, else = 1
                }

                return 1;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{ex}, Error executing P_GET_TRX_STATUS");
                _logger.LogError(ex, "Error executing P_GET_TRX_STATUS");
                return 1; // failure
            }
        }

        public async Task<string> RunSettlementJob(string thissessionID, decimal amount, string uploadID, string debitAcct, string creditAcct, string transCR)
        {
            string trxResponse = "";
            string narration = $"POS_MSC batch {uploadID}.";
            decimal amountInDecimal = Convert.ToDecimal(amount);
            string RealTranCodeNoCharge = _configuration["appConfiguration:RealTranCodeNoCharge"];

            trxResponse = await RunLocalFT(RealTranCodeNoCharge, thissessionID, debitAcct, creditAcct, narration, amountInDecimal, transCR);

            return trxResponse;
        }
        public async Task<string> RunLocalFT(string transType, string sessionid, string debitacctnum, string creditacctnum, string narration, decimal amount, string transCR)
        {
            JaizInternalService.processmessageSoapClient client = new JaizInternalService.processmessageSoapClient(0);
            JaizInternalService.fundtransfersingleitemRequest req = new JaizInternalService.fundtransfersingleitemRequest();
            string reqbody = await GetLocalFTBody(transType, sessionid, debitacctnum, creditacctnum, narration, amount);
            string ret = client.fundtransfersingleitem(reqbody);

            string responsecode = "";
            try
            {
                XmlDocument xml = new XmlDocument(); //using System.Xml.XmlDocument
                xml.LoadXml(ret);
                XmlNodeList xmlnode;
                int i = 0;
                xmlnode = xml.GetElementsByTagName("FTSingleResponse");
                for (i = 0; i <= xmlnode.Count - 1; i++)
                {
                    xmlnode[i].ChildNodes.Item(0).InnerText.Trim();
                    responsecode = xmlnode[i].ChildNodes.Item(1).InnerText.Trim();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{ex}, Unable to run local FT");
                _logger.LogError(ex, "Unable to run local FT");
            }

            await LogFTTransaction(sessionid, reqbody, ret, responsecode, transCR);

            return responsecode;
            //return "00";
        }

        public async Task<string> GetLocalFTBody(string trantype, string sessionid, string debitacctnum, string creditacctnum, string narration, decimal amount)
        {
            DateTime valD = DateTime.Now;
            string ChannelCode = _configuration["appConfiguration:ChannelCode"];
            string valuedate = valD.Day.ToString() + "/" + valD.Month.ToString() + "/" + valD.Year.ToString();
            string body = string.Empty;
            string filePath = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "localFT.txt");
            body = System.IO.File.ReadAllText(filePath);
            body = body.Replace("[TranType]", trantype);
            body = body.Replace("[SessionID]", sessionid);
            body = body.Replace("[ChannelCode]", ChannelCode);
            body = body.Replace("[DebitAcctNum]", debitacctnum);
            body = body.Replace("[CreditAcctNum]", creditacctnum);
            body = body.Replace("[Narration]", narration);
            body = body.Replace("[Amount]", amount.ToString());
            body = body.Replace("[ValueDate]", valuedate);
            body = body.Replace("100000", "100000.00");
            return body;
        }
        public async Task LogFTTransaction(string sessionId, string request, string response, string responseCode, string transCR)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                var log = new FTTransactionLogModel
                {
                    SessionId = sessionId,
                    Request = request,
                    Response = response,
                    ResponseCode = responseCode,
                    LogTime = DateTime.Now,
                    TransCredit = transCR,
                };

                db.FTTransactionLog.Add(log);
                await db.SaveChangesAsync();

            }
            catch (Exception ex)
            {

                Console.WriteLine($"{ex}, Unable to log FT transaction");
                _logger.LogError(ex, "Unable to log FT transaction");
            }

        }

    }
}
