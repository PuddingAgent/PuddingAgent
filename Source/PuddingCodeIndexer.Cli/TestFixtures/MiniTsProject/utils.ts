export function add(a: number, b: number): number {
    return a + b;
}

export interface Calculator {
    multiply(x: number, y: number): number;
}
