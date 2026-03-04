document.addEventListener('mousedown', function(e) {
    if (e.button === 1) {
        var gd = document.querySelector('.plotly-graph-div');
        var currentMode = gd.layout.dragmode;
        var newMode = currentMode === 'pan' ? 'zoom' : 'pan';
        Plotly.relayout(gd, {'dragmode': newMode});
    }
});

document.addEventListener('keydown', function(e) {
    var gd = document.querySelector('.plotly-graph-div');
    if (e.key === 'a') {
        Plotly.relayout(gd, {'dragmode': 'zoom'});
    } else if (e.key === 's') {
        Plotly.relayout(gd, {'dragmode': 'pan'});
    } else if (e.key === 'd') {
        Plotly.relayout(gd, {'dragmode': 'select'});
    }
});
