const priceFormatters = new Map<string, Intl.NumberFormat>()

// Currency formatting, shared across catalog + ordering (promoted to shared/ once a 2nd
// feature needed it — frontend/CLAUDE.md: promote on proven reuse, not speculatively).
export function formatPrice(amount: number, currency: string): string {
  let fmt = priceFormatters.get(currency)
  if (!fmt) {
    fmt = new Intl.NumberFormat(undefined, { style: 'currency', currency })
    priceFormatters.set(currency, fmt)
  }
  return fmt.format(amount)
}
