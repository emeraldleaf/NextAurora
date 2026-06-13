// Mirror of NextAurora.Contracts.DTOs.ProductDto (System.Text.Json camelCase on the wire).
export interface Product {
  id: string
  name: string
  description: string
  price: number
  currency: string
  category: string
  sellerId: string
  stockQuantity: number
  isAvailable: boolean
}
