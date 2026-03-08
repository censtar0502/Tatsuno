namespace Tatsuno.Model;

public enum TatsunoPumpCondition
{
    Unknown = -1,
    NozzleStored = 0,
    NozzleLifted = 1,
    Fuelling = 3,
    Finished = 4
}

public enum TatsunoPumpControllability
{
    Unknown = 0,
    PowerOn = 1,
    Controllable = 2,
    Uncontrollable = 3
}

public enum TatsunoUnitPriceFlag
{
    Unknown = 0,
    Invalid = 1,
    Valid = 2,
    Cash = 2,
    Credit = 3,
    SelectedAtPump = 9
}

public enum TatsunoProductCode
{
    None = 0,
    HighOctane = 1,
    Regular = 2,
    Diesel = 3,
    Kerosene = 4,
    LeadedHighOctane = 5,
    LeadedRegular = 6
}

public enum TatsunoIndicationType
{
    Unknown = 0,
    Primary = 1,
    Secondary = 2
}

public enum TatsunoAuthorizationTerm
{
    Normal = 0,
    VolumeLimited = 1,
    PresetChangeAllowed = 2,
    PresetChangeForbidden = 3
}

public enum TatsunoPresetKind
{
    Volume = 1,
    Amount = 2
}

public enum TatsunoLinkState
{
    Idle = 0,
    WaitingPollResponse = 1,
    WaitingSelectAck0 = 2,
    WaitingSelectAck1 = 3
}

public enum TatsunoCommandKind
{
    None = 0,
    AuthorizeSinglePrice = 1,
    AuthorizeMultiPrice = 2,
    CancelAuthorization = 3,
    LockPump = 4,
    ReleasePumpLock = 5,
    RequestStatus = 6,
    RequestTotals = 7,
    CrcAcknowledgment = 8
}
