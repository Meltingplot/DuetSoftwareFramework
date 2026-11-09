namespace UnitTests.FakeFirmware;

/// <summary>
/// RRF that the fake firmware models, see the milestones of design/sbc-link-v8-requirements.md (draft 8, section 7.2)
/// </summary>
public enum FakeRrfModel
{
    /// <summary>
    /// Fork RRF 0dccb9efe (v7 with RRF #43, #45 and #46): the SBC task carries InvalidateChannel out under the buffer mutex and refuses it while
    /// the main task holds that mutex (SbcInterface.cpp:868-925). It writes no tags and ignores those of DSF. This is the "stock RRF" of the tests
    /// </summary>
    V7,

    /// <summary>
    /// Milestone E1: the SBC task records InvalidateChannel and never refuses it, the main task applies it at the top of its next spin with the
    /// per-channel routine that a link loss runs as well (CE-7, CE-12, CE-25). It writes no tags and ignores those of DSF
    /// </summary>
    RecordApply
}
