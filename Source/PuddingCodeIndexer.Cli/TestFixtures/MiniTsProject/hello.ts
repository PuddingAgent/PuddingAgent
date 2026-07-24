import { add } from './utils';

export class Greeter {
    private name: string;
    
    constructor(name: string) {
        this.name = name;
    }
    
    greet(): string {
        return `Hello, ${this.name}! Count: ${add(1, 2)}`;
    }
}

export function createGreeter(name: string): Greeter {
    return new Greeter(name);
}
