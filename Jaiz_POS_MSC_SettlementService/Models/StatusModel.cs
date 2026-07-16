using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Jaiz_POS_MSC_SettlementService.Models
{
    public class StatusModel
    {
        public enum UploadStatus
        {
            Approved = 2,
            InBatchProgress = 3,
            Failed = 4,
            PartialOrFailed = 5,
            NoRecords = 6,
            BatchDebitSuccess = 7,
            InProgress = 8,
            Error = 9,
            Success = 10
        }

        public enum SettlementStatus
        {
            New = 2,
            InProgress = 3,
            Failed = 4,
            InvalidAmount = 9,
            Success = 10
        }
    }
}
