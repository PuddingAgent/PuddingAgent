from calc import add

class Greeter:
    def __init__(self, name: str):
        self.name = name
    
    def greet(self) -> str:
        return f"Hello, {self.name}! Count: {add(1, 2)}"

def create_greeter(name: str) -> Greeter:
    return Greeter(name)
