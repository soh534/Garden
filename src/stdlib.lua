-- Garden stdlib: generic combinators over the engine primitives.
-- Loaded before the user script. Everything here is pure composition of
-- roiVisible / queueAction / queueActionAt / waitMs — no engine state,
-- no references to any user-recorded action or ROI names. The user script
-- binds conveniences to its own recorded data, e.g.:
--     function clickIf(name, ms) return doIf(name, "click", ms) end

-- If roiName is visible: replay actionName offset to it, wait, return true.
function doIf(roiName, actionName, ms)
    if not roiVisible(roiName) then return false end
    queueActionAt(actionName, roiName)
    waitMs(ms or 3000)
    return true
end

-- Repeat actionName until targetRoi appears (checked before each repeat),
-- up to tries times. Returns true as soon as the target is seen.
-- No trailing check: the call sequence is check, action, wait — exactly N times at most.
function repeatUntilVisible(actionName, targetRoi, tries, ms)
    for i = 1, (tries or 4) do
        if roiVisible(targetRoi) then return true end
        queueAction(actionName)
        waitMs(ms or 3000)
    end
    return false
end

-- While roiName stays visible: replay actionName at it, wait.
-- Unbounded, matching the existing `while roiVisible(x) do ... end` semantics.
function drainWhileVisible(roiName, actionName, ms)
    while roiVisible(roiName) do
        queueActionAt(actionName, roiName)
        waitMs(ms or 3000)
    end
end
