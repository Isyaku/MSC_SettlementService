using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Jaiz_POS_MSC_SettlementService.Models
{
    public class UploadModel
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int ID { get; set; }
        public string? StaffID { get; set; }
        public string? UploadDate { get; set; }
        public string? Status { get; set; }
        public string? Comment { get; set; }
        public string? TransientAccount { get; set; }
        public string? CreditAccount { get; set; }
        public decimal? TotalAmount { get; set; }
        public string? Supervisor { get; set; }
        public string? TransactionId { get; set; }
    }
}
