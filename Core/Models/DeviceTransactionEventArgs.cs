using System;

namespace Core.Models
{
    public class DeviceTransactionEventArgs : EventArgs
    {
        public string EnrollNumber { get; set; }
        public bool IsInvalid { get; set; }
        public int AttendanceState { get; set; }
        public int VerifyMethod { get; set; }
        public DateTime TransactionTime { get; set; }
        public int WorkCode { get; set; }
        public DeviceInfo DeviceInfo { get; set; }
        public string TransactionType { get; set; }
        public bool IsRemoteTrigger { get; set; } = false;
    }
}

