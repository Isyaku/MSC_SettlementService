using Jaiz_POS_MSC_SettlementService.Models;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Jaiz_POS_MSC_SettlementService.Data
{
    public class AppDbContext : DbContext
    {
        public DbSet<MscRequestModel> MSC_Request { get; set; }

        public DbSet<UploadModel> MSC_Request_Upload { get; set; }
        public DbSet<FTTransactionLogModel> FTTransactionLog { get; set; }

        public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<MscRequestModel>().ToTable("MSC_Request");
            modelBuilder.Entity<UploadModel>().ToTable("MSC_Request_Upload");
            modelBuilder.Entity<FTTransactionLogModel>().ToTable("FT_TransactionLog");
        }
    }
}
