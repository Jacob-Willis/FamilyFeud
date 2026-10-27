Gameplay:

Choose team or random team to start
ask questions to the players
Team 1 goes first
If they get it right, they choose if they want to answer the rest of the answers or pass to the other team
	Keep the turn:
		If they get three wrong, it passes to team 2
		Team 2 goes next
			if they get one right, they steal all the points from team 1 and their answer
			if they get it wrong, team 1 keeps the points
	Pass the turn:
		Team 2 anwswer the question
		If they get three wrong, it passes to team 1
			if team 1 gets it right, they steal all the points from team 2 and their answer

Team 2 goes next
	repeat as above for next question


1. it should be selectedFirst. If this is random, the code should already be in place to randomly select a team. Please confirm
2. You shouldn't tie on first answer. Once it is revealed, it cannot be revealed again
3. Collect the points in a pool which is shown on audience and game/host pages
4. Host will click strike if team answer is wrong, and points should move automatically to the right team at end of round. Host will manually select play/pass depending on what the team says they want
5. The next team to start should be the opposite team that started, i.e after runnining PlayerTurn.Switch()