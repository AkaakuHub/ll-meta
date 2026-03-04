using System.Collections.Generic;
using Silk.NET.OpenXR;
using XrAction = Silk.NET.OpenXR.Action;

namespace LLMeta.App.Services;

public sealed unsafe partial class OpenXrControllerInputService
{
    private Result InitializeActions()
    {
        if (_xr is null)
        {
            return Result.ErrorHandleInvalid;
        }

        var actionSetCreateInfo = new ActionSetCreateInfo
        {
            Type = StructureType.ActionSetCreateInfo,
            Priority = 0,
        };
        var actionSetName = actionSetCreateInfo.ActionSetName;
        WriteFixedUtf8(actionSetName, (int)XR.MaxActionSetNameSize, "llmeta_input");
        var localizedActionSetName = actionSetCreateInfo.LocalizedActionSetName;
        WriteFixedUtf8(
            localizedActionSetName,
            (int)XR.MaxLocalizedActionSetNameSize,
            "LLMeta Input"
        );
        var createActionSetResult = _xr.CreateActionSet(
            _instance,
            ref actionSetCreateInfo,
            ref _actionSet
        );
        if (createActionSetResult != Result.Success)
        {
            return createActionSetResult;
        }

        var createLeftStickResult = CreateAction(
            _actionSet,
            "left_stick",
            "Left Stick",
            ActionType.Vector2fInput,
            ref _leftStickAction
        );
        if (createLeftStickResult != Result.Success)
        {
            return createLeftStickResult;
        }

        var createRightStickResult = CreateAction(
            _actionSet,
            "right_stick",
            "Right Stick",
            ActionType.Vector2fInput,
            ref _rightStickAction
        );
        if (createRightStickResult != Result.Success)
        {
            return createRightStickResult;
        }

        var createLeftXResult = CreateAction(
            _actionSet,
            "left_x",
            "Left X",
            ActionType.BooleanInput,
            ref _leftXAction
        );
        if (createLeftXResult != Result.Success)
        {
            return createLeftXResult;
        }

        var createLeftYResult = CreateAction(
            _actionSet,
            "left_y",
            "Left Y",
            ActionType.BooleanInput,
            ref _leftYAction
        );
        if (createLeftYResult != Result.Success)
        {
            return createLeftYResult;
        }

        var createRightAResult = CreateAction(
            _actionSet,
            "right_a",
            "Right A",
            ActionType.BooleanInput,
            ref _rightAAction
        );
        if (createRightAResult != Result.Success)
        {
            return createRightAResult;
        }

        var createRightBResult = CreateAction(
            _actionSet,
            "right_b",
            "Right B",
            ActionType.BooleanInput,
            ref _rightBAction
        );
        if (createRightBResult != Result.Success)
        {
            return createRightBResult;
        }

        var createLeftTriggerResult = CreateAction(
            _actionSet,
            "left_trigger",
            "Left Trigger",
            ActionType.FloatInput,
            ref _leftTriggerAction
        );
        if (createLeftTriggerResult != Result.Success)
        {
            return createLeftTriggerResult;
        }

        var createLeftGripResult = CreateAction(
            _actionSet,
            "left_grip",
            "Left Grip",
            ActionType.FloatInput,
            ref _leftGripAction
        );
        if (createLeftGripResult != Result.Success)
        {
            return createLeftGripResult;
        }

        var createRightTriggerResult = CreateAction(
            _actionSet,
            "right_trigger",
            "Right Trigger",
            ActionType.FloatInput,
            ref _rightTriggerAction
        );
        if (createRightTriggerResult != Result.Success)
        {
            return createRightTriggerResult;
        }

        var createRightGripResult = CreateAction(
            _actionSet,
            "right_grip",
            "Right Grip",
            ActionType.FloatInput,
            ref _rightGripAction
        );
        if (createRightGripResult != Result.Success)
        {
            return createRightGripResult;
        }

        var createLeftStickClickResult = CreateAction(
            _actionSet,
            "left_stick_click",
            "Left Stick Click",
            ActionType.BooleanInput,
            ref _leftStickClickAction
        );
        if (createLeftStickClickResult != Result.Success)
        {
            return createLeftStickClickResult;
        }

        var createRightStickClickResult = CreateAction(
            _actionSet,
            "right_stick_click",
            "Right Stick Click",
            ActionType.BooleanInput,
            ref _rightStickClickAction
        );
        if (createRightStickClickResult != Result.Success)
        {
            return createRightStickClickResult;
        }

        var bindings = new List<ActionSuggestedBinding>(16);

        var addResult = AddRequiredBinding(
            bindings,
            _leftStickAction,
            "/user/hand/left/input/thumbstick"
        );
        if (addResult != Result.Success)
        {
            return addResult;
        }

        addResult = AddRequiredBinding(
            bindings,
            _rightStickAction,
            "/user/hand/right/input/thumbstick"
        );
        if (addResult != Result.Success)
        {
            return addResult;
        }

        addResult = AddRequiredBinding(bindings, _leftXAction, "/user/hand/left/input/x/click");
        if (addResult != Result.Success)
        {
            return addResult;
        }

        addResult = AddRequiredBinding(bindings, _leftYAction, "/user/hand/left/input/y/click");
        if (addResult != Result.Success)
        {
            return addResult;
        }

        addResult = AddRequiredBinding(bindings, _rightAAction, "/user/hand/right/input/a/click");
        if (addResult != Result.Success)
        {
            return addResult;
        }

        addResult = AddRequiredBinding(bindings, _rightBAction, "/user/hand/right/input/b/click");
        if (addResult != Result.Success)
        {
            return addResult;
        }

        addResult = AddRequiredBinding(
            bindings,
            _leftTriggerAction,
            "/user/hand/left/input/trigger/value"
        );
        if (addResult != Result.Success)
        {
            return addResult;
        }

        addResult = AddRequiredBinding(
            bindings,
            _leftGripAction,
            "/user/hand/left/input/squeeze/value"
        );
        if (addResult != Result.Success)
        {
            return addResult;
        }

        addResult = AddRequiredBinding(
            bindings,
            _rightTriggerAction,
            "/user/hand/right/input/trigger/value"
        );
        if (addResult != Result.Success)
        {
            return addResult;
        }

        addResult = AddRequiredBinding(
            bindings,
            _rightGripAction,
            "/user/hand/right/input/squeeze/value"
        );
        if (addResult != Result.Success)
        {
            return addResult;
        }

        const string interactionProfilePath = "/interaction_profiles/oculus/touch_controller";
        var requiredSuggestionResult = SuggestBindingForProfile(
            _instance,
            interactionProfilePath,
            bindings.ToArray()
        );
        if (requiredSuggestionResult != Result.Success)
        {
            return requiredSuggestionResult;
        }

        var optionalSupported = new List<string>(2);
        var optionalUnsupported = new List<string>(2);
        TryAddOptionalBindingWithSuggest(
            bindings,
            _leftStickClickAction,
            "/user/hand/left/input/thumbstick/click",
            interactionProfilePath,
            optionalSupported,
            optionalUnsupported
        );
        TryAddOptionalBindingWithSuggest(
            bindings,
            _rightStickClickAction,
            "/user/hand/right/input/thumbstick/click",
            interactionProfilePath,
            optionalSupported,
            optionalUnsupported
        );

        _bindingSupportSummary =
            $"optionalSupported={optionalSupported.Count}, optionalUnsupported={optionalUnsupported.Count}";
        if (optionalUnsupported.Count > 0)
        {
            _bindingSupportSummary =
                $"{_bindingSupportSummary} | unsupported: {string.Join(", ", optionalUnsupported)}";
        }

        var attachInfo = new SessionActionSetsAttachInfo
        {
            Type = StructureType.SessionActionSetsAttachInfo,
            CountActionSets = 1,
        };
        var actionSet = _actionSet;
        attachInfo.ActionSets = &actionSet;
        return _xr.AttachSessionActionSets(_session, ref attachInfo);
    }
}
