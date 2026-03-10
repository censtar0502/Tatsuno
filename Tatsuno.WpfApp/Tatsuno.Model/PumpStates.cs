namespace Tatsuno.Model;

/// <summary>
/// Operational state of a pump/post
/// </summary>
public enum PumpOperationalState
{
    Unknown = 0,
    Idle = 1,
    NozzleLifted = 2,
    PricePending = 3,
    PresetPending = 4,
    Authorized = 5,
    Fuelling = 6,
    Finished = 7,
    TotalsRequested = 8,
    Completed = 9,
    Error = 10
}

/// <summary>
/// Communication layer state machine
/// </summary>
public enum LinkLayerState
{
    Idle = 0,
    SendPoll = 1,
    WaitPollReply = 2,
    PollFrameReceived = 3,
    SendAck1 = 4,
    SendSelectEnq = 5,
    WaitDle0 = 6,
    SendCommandText = 7,
    WaitDle1OrNak = 8,
    ResumePolling = 9,
    Retry = 10,
    Fault = 11
}

/// <summary>
/// Transaction type for fueling operations
/// </summary>
public enum TransactionType
{
    None = 0,
    AuthorizeByAmount = 1,
    AuthorizeByVolume = 2,
    ManualStop = 3,
    CancelAuthorization = 4
}

/// <summary>
/// Condition/status flags from dispenser
/// </summary>
[Flags]
public enum PumpCondition
{
    Normal = 0x00,
    Error = 0x01,
    MaintenanceRequired = 0x02,
    OutOfProduct = 0x04,
    NozzleNotLowered = 0x08,
    EmergencyStop = 0x10
}
