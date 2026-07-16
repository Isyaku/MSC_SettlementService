using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Jaiz_POS_MSC_SettlementService.Models
{
    public class FTTransactionLogModel
    {
        [Key]
        public int RequestId { get; set; }
        public string SessionId { get; set; }
        public string Request { get; set; }
        public string Response { get; set; }
        public string ResponseCode { get; set; }
        public DateTime LogTime { get; set; }
        public string TransCredit { get; set; }
    }
}
