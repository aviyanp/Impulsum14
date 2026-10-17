// OSDKSettings (component 2249 = 0x08C9) — not in our Blaze3SDK. The client calls
// fetchSettings (cmd 1) and fetchSettingsGroups (cmd 2) at the main menu (ticker/settings).
// Empty responses are fine (no settings to push); we register the component so they're
// answered properly instead of falling through to the generic UNHANDLED fallback.
// TDF tags from ZamboniCommonComponents: FetchSettingsResponse{LSIN,LSST},
// FetchSettingsGroupsResponse{LGRP}. Lists left empty (element type irrelevant when empty).
using Blaze.Core;
using EATDF;
using EATDF.Members;
using EATDF.Types;

namespace Impulsum14;

public sealed class FetchSettingsResponse : Tdf
{
    private static readonly TdfMemberInfo[] __typeInfos =
    [
        new TdfMemberInfo("IntegerSettingList", "mIntegerSettingList", 0xB33A6E00, TdfType.List, 0, true), // LSIN
        new TdfMemberInfo("StringSettingList", "mStringSettingList", 0xB33CF400, TdfType.List, 1, true),   // LSST
    ];
    private readonly ITdfMember[] __members;
    private readonly TdfList<EmptyMessage> _ints = new(__typeInfos[0]);
    private readonly TdfList<EmptyMessage> _strs = new(__typeInfos[1]);

    public FetchSettingsResponse() { __members = [_ints, _strs]; }

    public override Tdf CreateNew() => new FetchSettingsResponse();
    public override ITdfMember[] GetMembers() => __members;
    public override TdfMemberInfo[] GetMemberInfos() => __typeInfos;
    public override string GetClassName() => "FetchSettingsResponse";
    public override string GetFullClassName() => "Blaze::OSDKSettings::FetchSettingsResponse";
}

public sealed class FetchSettingsGroupsResponse : Tdf
{
    private static readonly TdfMemberInfo[] __typeInfos =
    [
        new TdfMemberInfo("SettingGroupList", "mSettingGroupList", 0xB27CB000, TdfType.List, 0, true), // LGRP
    ];
    private readonly ITdfMember[] __members;
    private readonly TdfList<EmptyMessage> _groups = new(__typeInfos[0]);

    public FetchSettingsGroupsResponse() { __members = [_groups]; }

    public override Tdf CreateNew() => new FetchSettingsGroupsResponse();
    public override ITdfMember[] GetMembers() => __members;
    public override TdfMemberInfo[] GetMemberInfos() => __typeInfos;
    public override string GetClassName() => "FetchSettingsGroupsResponse";
    public override string GetFullClassName() => "Blaze::OSDKSettings::FetchSettingsGroupsResponse";
}

internal sealed class OsdkSettingsComponent : BlazeComponent
{
    public override ushort Id => 2249;
    public override string Name => "OSDKSettingsComponent";
    public override string GetErrorName(ushort errorCode) => $"0x{errorCode:X4}";

    public OsdkSettingsComponent()
    {
        RegisterCommand(new RpcCommandFunc<EmptyMessage, FetchSettingsResponse, EmptyMessage>
        {
            Id = 1, // fetchSettings
            Name = "fetchSettings",
            IsSupported = true,
            Func = (req, ctx) =>
            {
                ServerDiagnostics.Write("[OSDKSettings] fetchSettings");
                return Task.FromResult<Tdf>(new FetchSettingsResponse());
            },
        });

        RegisterCommand(new RpcCommandFunc<EmptyMessage, FetchSettingsGroupsResponse, EmptyMessage>
        {
            Id = 2, // fetchSettingsGroups
            Name = "fetchSettingsGroups",
            IsSupported = true,
            Func = (req, ctx) =>
            {
                ServerDiagnostics.Write("[OSDKSettings] fetchSettingsGroups");
                return Task.FromResult<Tdf>(new FetchSettingsGroupsResponse());
            },
        });
    }
}
