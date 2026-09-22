using System;

namespace HRSystem.Models
{
    public class DvrDevice
    {
        public int Id { get; set; }
        public int BranchId { get; set; }
        public string DvrName { get; set; }
        public string LocalIp { get; set; }
        public int VpnPort { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
        public DateTime? LastSyncTime { get; set; }
        public string Status { get; set; }

        public virtual Branch Branch { get; set; }
    }
}
