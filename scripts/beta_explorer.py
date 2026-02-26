import numpy as np
import plotly.graph_objects as go
from plotly.subplots import make_subplots
from scipy.stats import beta

x = np.linspace(0.001, 0.999, 500)

configs = [
    # (a, b, description)
    (0.5, 0.5, "U-shaped, both extremes"),
    (0.3, 0.1, "U-shaped, biased toward 1"),
    (0.5, 0.2, "U-shaped, biased toward 1"),
    (0.8, 0.2, "Biased toward 1, moderate"),
    (2.0, 0.5, "Biased toward 1, sum=2.5"),
    (4.0, 1.0, "Biased toward 1, sum=5"),
    (8.0, 2.0, "Biased toward 1, sum=10"),
    (2.0, 2.0, "Symmetric hill"),
]

fig = make_subplots(rows=4, cols=2, subplot_titles=[
    f"Beta({a},{b}) — {desc}" for a, b, desc in configs
])

for i, (a, b, desc) in enumerate(configs):
    row = i // 2 + 1
    col = i % 2 + 1
    y = beta.pdf(x, a, b)
    fig.add_trace(
        go.Scatter(x=x, y=y, mode='lines', name=f"Beta({a},{b})",
                   line=dict(width=2)),
        row=row, col=col
    )

fig.update_layout(height=1200, width=900, title_text="Beta Distribution Shapes", showlegend=False)
fig.write_html("/home/mrakgr/Trading-Edge/scripts/beta_explorer.html")
print("Written to scripts/beta_explorer.html")
