using System.Collections.Generic;
using Silk.NET.OpenXR;
using XrAction = Silk.NET.OpenXR.Action;

namespace LLMeta.App.Services;

public sealed unsafe partial class OpenXrControllerInputService
{
    private Result AddRequiredBinding(
        List<ActionSuggestedBinding> bindings,
        XrAction action,
        string pathString
    )
    {
        var pathResult = StringToPath(_instance, pathString, out var path);
        if (pathResult != Result.Success)
        {
            return pathResult;
        }

        bindings.Add(new ActionSuggestedBinding { Action = action, Binding = path });
        return Result.Success;
    }

    private void TryAddOptionalBindingWithSuggest(
        List<ActionSuggestedBinding> bindings,
        XrAction action,
        string pathString,
        string interactionProfilePath,
        List<string> optionalSupported,
        List<string> optionalUnsupported
    )
    {
        var pathResult = StringToPath(_instance, pathString, out var path);
        if (pathResult != Result.Success)
        {
            optionalUnsupported.Add(pathString);
            return;
        }

        var candidate = new ActionSuggestedBinding { Action = action, Binding = path };
        bindings.Add(candidate);

        var validationResult = SuggestBindingForProfile(
            _instance,
            interactionProfilePath,
            bindings.ToArray()
        );
        if (validationResult == Result.Success)
        {
            optionalSupported.Add(pathString);
            return;
        }

        bindings.RemoveAt(bindings.Count - 1);
        optionalUnsupported.Add($"{pathString}({validationResult})");
    }

    private Result SyncActions()
    {
        if (_xr is null)
        {
            return Result.ErrorHandleInvalid;
        }

        var activeActionSet = new ActiveActionSet
        {
            ActionSet = _actionSet,
            SubactionPath = XR.NullPath,
        };
        var syncInfo = new ActionsSyncInfo
        {
            Type = StructureType.ActionsSyncInfo,
            CountActiveActionSets = 1,
            ActiveActionSets = &activeActionSet,
        };
        return _xr.SyncAction(_session, ref syncInfo);
    }

    private unsafe Result CreateAction(
        ActionSet actionSet,
        string actionName,
        string localizedActionName,
        ActionType actionType,
        ref XrAction action
    )
    {
        if (_xr is null)
        {
            return Result.ErrorHandleInvalid;
        }

        var actionCreateInfo = new ActionCreateInfo
        {
            Type = StructureType.ActionCreateInfo,
            ActionType = actionType,
        };
        var internalActionName = actionCreateInfo.ActionName;
        WriteFixedUtf8(internalActionName, (int)XR.MaxActionNameSize, actionName);
        var internalLocalizedActionName = actionCreateInfo.LocalizedActionName;
        WriteFixedUtf8(
            internalLocalizedActionName,
            (int)XR.MaxLocalizedActionNameSize,
            localizedActionName
        );
        return _xr.CreateAction(actionSet, ref actionCreateInfo, ref action);
    }

    private unsafe Result SuggestBindingForProfile(
        Instance instance,
        string interactionProfilePath,
        ActionSuggestedBinding[] bindings
    )
    {
        if (_xr is null)
        {
            return Result.ErrorHandleInvalid;
        }

        var interactionProfilePathResult = StringToPath(
            instance,
            interactionProfilePath,
            out var interactionProfile
        );
        if (interactionProfilePathResult != Result.Success)
        {
            return interactionProfilePathResult;
        }

        fixed (ActionSuggestedBinding* suggestedBindings = bindings)
        {
            var suggestedBinding = new InteractionProfileSuggestedBinding
            {
                Type = StructureType.InteractionProfileSuggestedBinding,
                InteractionProfile = interactionProfile,
                CountSuggestedBindings = (uint)bindings.Length,
                SuggestedBindings = suggestedBindings,
            };
            return _xr.SuggestInteractionProfileBinding(instance, ref suggestedBinding);
        }
    }
}
