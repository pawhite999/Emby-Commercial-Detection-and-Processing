namespace CommDetect.Core;

/// <summary>Type of signal produced by a detector.</summary>
public enum SignalType
{
    BlackFrame,
    SceneChange,
    Silence,
    LogoAbsence,
    AspectRatioChange,
    AudioRepetition,
    Letterbox
}

/// <summary>Classification of a media segment.</summary>
public enum SegmentType
{
    Commercial,
    Program
}

/// <summary>Output file format for detection results.</summary>
public enum OutputFormat
{
    Edl,
    ComskipTxt,
    MkvChapters,
    Json,
    FFMetadata
}
