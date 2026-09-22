namespace HRSystem.ModelView
{
    public class AttendanceVM
    {
        public string SearchName { get; set; }
        public string EmployeeNo { get; set; }
        public string BranchName { get; set; }
        public string DeviceName { get; set; }
        public DateTime LogTime { get; set; }
        public string Direction { get; set; }
        
        // Calculated Shift Metrics
        public double? DelayMinutes { get; set; }
        public double? OvertimeMinutes { get; set; }
        public string StartShift { get; set; }
        public string EndShift { get; set; }
        public bool IsCalculated { get; set; }
    }
}
