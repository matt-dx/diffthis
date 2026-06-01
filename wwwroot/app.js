window.scrollToElement = (id, fallbackId) => {
    const el = document.getElementById(id)
            ?? (fallbackId ? document.getElementById(fallbackId) : null);
    el?.scrollIntoView({ behavior: 'smooth', block: 'start' });
};

window.setTheme = (theme) => {
    document.documentElement.setAttribute('data-theme', theme);
};

// Module-level variable — always points to the current live AnalysisPanel dotnet ref.
// The body listener is registered once at script load; it just checks this variable.
let _analysisRefDotNet = null;

// One global listener, registered at load time (before any component is mounted).
// capture:true fires before Blazor's NavigationManager intercepts anchor-like clicks.
document.body.addEventListener('click', e => {
    if (!_analysisRefDotNet) return;
    const el = e.target.closest('.analysis-ref');
    if (!el) return;
    e.preventDefault();
    e.stopPropagation();
    _analysisRefDotNet.invokeMethodAsync('OnRefClicked', el.dataset.ref)
        .catch(() => { /* component may be mid-dispose; ignore */ });
}, { capture: true });

// Called by AnalysisPanel.OnAfterRenderAsync to register (or re-register) the live ref.
window.setAnalysisRefDotNet = (dotnetRef) => {
    _analysisRefDotNet = dotnetRef;
};

// Called by AnalysisPanel.DisposeAsync to prevent stale invocations after disposal.
window.clearAnalysisRefDotNet = () => {
    _analysisRefDotNet = null;
};
