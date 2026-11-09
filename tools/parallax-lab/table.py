# Print the measures, Ed's flagged examples first, then the rest sorted by edge gap.
import json, sys
rows = json.load(open(sys.argv[1]))
flagged = ['Luxor005', 'front wheel', 'BudapestParliament', '20250408_bing', 'YosemiteWinter', 'FrenchRivieraVillage']
def show(r): print(f"{r['file'][:52]:52} a{r['area']:.2f} v{r['vague']:.2f} s{r['sides']:.2f} edge{r['edge']:+.2f} gap{r['gap']:+.2f} tex{r['tex']:.3f}")
print('--- flagged by Ed')
for r in rows:
    if any(f in r['file'] for f in flagged): show(r)
print('--- the rest, edge gap high to low')
for r in sorted(rows, key=lambda r: -r['edge']):
    if not any(f in r['file'] for f in flagged): show(r)
