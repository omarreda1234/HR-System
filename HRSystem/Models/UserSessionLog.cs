using System;
using System.ComponentModel.DataAnnotations;

namespace HRSystem.Models
{
    public class UserSessionLog
    {
        [Key]
        public int Id { get; set; }
        public string Email { get; set; }
        public string IpAddress { get; set; }
        public string ComputerName { get; set; }
        public string DeviceDetails { get; set; }
        public DateTime AccessTime { get; set; }
        public string ActionAccessed { get; set; }
    }
}
