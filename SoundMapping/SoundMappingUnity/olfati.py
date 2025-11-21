# Main simulation file called by the Webots

import numpy as np
import rclpy
import os
import yaml
from crazyflie_webots_sim.pid_control import pid_velocity_fixed_height_controller
from geometry_msgs.msg import Twist
from hri_swarm_interfaces.msg import SwarmState
from rclpy.time import Time
from ament_index_python.packages import get_package_share_directory


# Crazyflie drone class in webots
class SwarmPilot():
    def init(self, webots_node, properties):
        super().__init__()

        # ROS interface
        rclpy.init(args=None)
        self.__node = rclpy.create_node('swarm_pilot')
        
        self.__robot = webots_node.robot
        self.__timestep = int(self.__robot.getBasicTimeStep())


        # Load parameters from the config file
        config_file = os.path.join(get_package_share_directory('crazyflie_webots_sim'), 'config', 'params.yaml')
        with open(config_file, 'r') as f:
            params = yaml.safe_load(f)
            
        # Set the parameters
        self.num_drones = params['/**']['ros__parameters']['num_drones']
        self.num_cylinders = params['/swarm_pilot']['ros__parameters']['num_cylinders']
        self.cylinder_radius = params['/swarm_pilot']['ros__parameters']['cylinder_radius']
        self.target_separation = params['/swarm_pilot']['ros__parameters']['swarm_separation']
        self.takeoff_height = params['/swarm_pilot']['ros__parameters']['takeoff_height']
        test_folder = params['/**']['ros__parameters']['test_folder']
        test_subject = params['/**']['ros__parameters']['test_subject']
        test_number = params['/**']['ros__parameters']['test_number']

        self.count = 0

        self.time = 0.00
        self.dt = 1

        self.viewpoint_default_position = np.array([-4, 0, 1])

        # Create the log file
        log_file_name = os.path.join('src/symbiotic_swarm/crazyflie_webots_sim/data', test_folder, test_subject, test_number, 'swarm_metrics.txt')
        self.log_file = open(log_file_name, 'w')    
        
        # Write header to log file
        self.log_file.write('Time, Avg_x_pos, Avg_y_pos, Avg_z_pos, Avg_x_vel, Avg_y_vel, Avg_z_vel, Avg_Separation\n')

        # Subscribe to the cmd_vel topic
        self.__cmd_vel = Twist()
        self.__node.create_subscription(Twist, 'cmd_vel', self.__cmd_vel_callback, 1)

        # Create a publisher for the swarm state
        # self.swarm_state = SwarmState()   # TODO: Check if this is better convention than setting a new variable each step
        self.__swarm_publisher = self.__node.create_publisher(SwarmState, '/swarm_state', 1)

        # Create a boolean array to check if each drone is still in the swarm
        self.drone_in_swarm = np.ones(self.num_drones, dtype=bool).tolist()
        
        # Set takeoff indicator
        self.takeoff = False

        # Set the initial swarm position
        self.swarm_prev_pos = np.array([0,0,0])

        # Create a cmd_vel publisher for each drone
        self.__drone_publishers = []
        for i in range(self.num_drones):
            topic_name = '/CRAZYFLIE_'+str(i+1)+'/cmd_vel'
            self.__drone_publishers.append(self.__node.create_publisher(Twist, topic_name, 10))

    # Callback function for the cmd_vel topic
    def __cmd_vel_callback(self, msg):
        self.__cmd_vel = msg

        # Set the swarm target separation by reading the x component of the angular velocity of the cmd_vel message
        # TODO: Update this to a more elegant solution, such as a custom message type
        self.target_separation = self.__cmd_vel.angular.x
    
    # Get the position and velocity of every drone and store in a numpy array
    def get_drone_poses(self):
        drone_poses = []
        for i in range(self.num_drones):
            drone_node = self.__robot.getFromDef('CRAZYFLIE_'+str(i+1))
            drone_euler = self.get_euler(drone_node.getOrientation())
            drone_vel = drone_node.getVelocity()
            drone_pose = [drone_node.getPosition(), drone_vel[0:3], drone_euler, drone_vel[3:6]]
            drone_poses.append(drone_pose)
    
        return np.array(drone_poses)
        
    # Define a function to check if the drone is spinning out of control
    def check_safe_spin(self, drone_pose):
        drone_ang_vel = drone_pose[3]
        if abs(drone_ang_vel[0]) > 1 or abs(drone_ang_vel[1]) > 1 or abs(drone_ang_vel[2]) > 1:
            return False
        else:
            return True
    
    # Get the average position of every drone that isn't upside down or too far away from the swarm
    def get_average_drone_pos(self, drone_poses, prev_avg_pos):

        # Initialize
        avg_pos = np.zeros(3)
        num_upright_drones = 0
        dist_tolerance = 3 * self.target_separation

        for drone_pose in drone_poses:
        
            # Check if the drone is too far away from the previous average
            if np.linalg.norm(drone_pose[0] - prev_avg_pos) > dist_tolerance:
                continue
            
            # Check if the drone is upside down
            if np.abs(drone_pose[2][0]) > np.pi/4 or np.abs(drone_pose[2][1]) > np.pi/4:
                continue

            # Check if the drone is spinning out of control
            if not self.check_safe_spin(drone_pose):
                continue

            # Add the drone position to the average
            avg_pos += drone_pose[0]
            num_upright_drones += 1

        # Divide by the number of drones
        if num_upright_drones == 0:
            return avg_pos
        else:
            return avg_pos / num_upright_drones
    
    # Get the average yaw of every drone that isn't upside down or too far away from the swarm
    def get_average_yaw(self, drone_poses):

        # Initialize
        avg_yaw = 0
        num_upright_drones = 0
        dist_tolerance = 3 * self.target_separation

        # Convert the drone yaws to a unit vector
        unit_vecs = np.array([(np.cos(drone_pose[2][2]), np.sin(drone_pose[2][2])) for drone_pose in drone_poses])
        avg_unit_vec = 0

        for idx, drone_pose in enumerate(drone_poses):
        
            # Check if the drone is too far away from the previous average
            if np.linalg.norm(drone_pose[0] - self.swarm_prev_pos) > dist_tolerance:
                continue
            
            # Check if the drone is upside down
            if np.abs(drone_pose[2][0]) > np.pi/4 or np.abs(drone_pose[2][1]) > np.pi/4:
                continue

            # Check if the drone is spinning out of control
            if not self.check_safe_spin(drone_pose):
                continue

            # Add the drone position to the average
            avg_unit_vec += unit_vecs[idx]
            num_upright_drones += 1

        # Divide by the number of drones
        if num_upright_drones != 0:
            avg_unit_vec /= num_upright_drones

        # Get the average yaw from the unit vector
        if len(avg_unit_vec) > 0:    
            avg_yaw = np.arctan2(avg_unit_vec[1], avg_unit_vec[0])
        else:
            avg_yaw = 0

        # Make sure the average yaw is between -pi and pi
        if avg_yaw > np.pi:
            avg_yaw -= 2*np.pi
        elif avg_yaw < -np.pi:
            avg_yaw += 2*np.pi
        
        return avg_yaw
    
    # Get the orientation of a drone in euler angles from the rotation matrix
    def get_euler(self, R):
        phi = np.arctan2(R[7], R[8])
        theta = np.arctan2(-R[6], np.sqrt(R[7]**2 + R[8]**2))
        psi = np.arctan2(R[3], R[0])
        return np.array([phi, theta, psi])

    # Rotate the control commands from the global refernce frame to the drone reference frame
    def rot_global2body(self, control_commands, yaw):
        ctrl_x = control_commands[0] * np.cos(yaw) + control_commands[1] * np.sin(yaw)
        ctrl_y = -control_commands[0] * np.sin(yaw) + control_commands[1] * np.cos(yaw)
        ctrl_z = control_commands[2]
        
        return [ctrl_x, ctrl_y, ctrl_z]
    
    # Rotate the control commands from the drone refernce frame to the global reference frame
    def rot_body2global(self, control_commands, yaw):
        yaw = -yaw
        
        return self.rot_global2body(control_commands, yaw)
    
    # Get the position of every cylinder obstacle and store in a numpy array
    def get_cylinder_positions(self):
        obstacle_poses = []
        for i in range(self.num_cylinders):
            obstacle_node = self.__robot.getFromDef('CYLINDER_'+str(i+1))
            obstacle_pose = obstacle_node.getPosition()
            obstacle_pose[2] = 0.5
            obstacle_poses.append(obstacle_pose)
        
        return np.array(obstacle_poses)
    
    # Calculate the cohesion intensity for the Olfati-Saber model
    def get_cohesion_intensity(self, r, d_ref, a, b, c):
        
        diff = r - d_ref
        return ((a+b)/2 * (np.sqrt(1+(diff + c)**2) - np.sqrt(1+c**2)) + (a-b)*diff/2)
    
    # Calculate the cohesion intensity derivative for the Olfati-Saber model
    def get_cohesion_intensity_der(self, r, d_ref, a, b, c):
            
        diff = r - d_ref
        return (a+b)/2 * (diff + c) / np.sqrt(1+(diff + c)**2) + (a-b)/2

    # Calcualte the neighbour weight for the Olfati-Saber model
    def get_neighbour_weight(self, r, r0, delta):

        r_ratio = r / r0

        if r_ratio < delta:
            return 1
        elif r_ratio < 1:
            return 0.25 * (1 + np.cos(np.pi * (r_ratio - delta) / (1 - delta)))**2  #with k=2
        else:
            return 0
    
    # Calcualte the derivative of the neighbour weight for the Olfati-Saber model
    def get_neighbour_weight_der(self, r, r0, delta):
        
        r_ratio = r/r0

        if r_ratio < delta:
            return 0
        elif r_ratio < 1:
            arg = np.pi * (r_ratio - delta) / (1 - delta)
            return 1/2*(-np.pi/(1-delta))*(1+np.cos(arg))*np.sin(arg)
        else:
            return 0
    
    # Calculate the attraction/repulsion force for the Olfati-Saber model
    def get_cohesion_force(self, r, d_ref, a, b, c, r0, delta):
        
        return 1/r0 * self.get_neighbour_weight_der(r, r0, delta) * self.get_cohesion_intensity(r, d_ref, a, b, c) + self.get_neighbour_weight(r, r0, delta) * self.get_cohesion_intensity_der(r, d_ref, a, b, c)
    
    # Calculate the maximum velocity effect for the Vasarhelyi model - update to not be private??
    def get_v_max(self, v, r, a, p):
        
        # Compute the velocity decay function
        if r <= 0:
            v_max =  0
        elif 0 < r*p and r*p < a/p:
            v_max = r*p
        else:
            v_max = np.sqrt(2*a*r - (a**2)/(p**2))

        return max(v, v_max)
         
    # Function to get the average seperation distance between drones
    def get_average_seperation(self, drone_poses):
        avg_sep = 0
        for i in range(self.num_drones):
            for j in range(i+1, self.num_drones):
                avg_sep += np.linalg.norm(drone_poses[i][0] - drone_poses[j][0])
        avg_sep /= (self.num_drones * (self.num_drones + 1) / 2)
        return avg_sep

    # Compute the velocity command to send to each drone using the Reynolds flocking algorithm
    def reynolds_input(self, drone_id, drone_pose, neighbour_poses, cylinder_poses):
        
        drone_pos = drone_pose[0]
        drone_vel = drone_pose[1]
        
        neighbour_pos = [neighbour_pose[0] for neighbour_pose in neighbour_poses]
        neighbour_vel = [neighbour_pose[1] for neighbour_pose in neighbour_poses]
        num_neighbours = len(neighbour_pos)
            
        # Define coefficients for each rule
        cohesion_strength = 0.035
        separation_strength = 0.007
        alignment_strength = 0  #0.05

        # Initialize the Reynolds swarm commands
        cohesion = np.zeros(3)
        separation = np.zeros(3)
        alignment = np.zeros(3)

        # Go through all the neighbours of the drone and compute the Reynolds swarm commands
        if num_neighbours > 0:

            # Loop through the neighbours
            for i in range(num_neighbours):
                diff = neighbour_pos[i] - drone_pos
                diff_vel = drone_vel - neighbour_vel[i]
                
                cohesion += diff
                separation -= diff / (np.linalg.norm(diff)**2)
                alignment += diff_vel

            # Multiply by coefficients and divide by number of neighbours
            cohesion *= cohesion_strength/num_neighbours
            separation *= separation_strength/num_neighbours
            alignment *= alignment_strength/num_neighbours

        # Rule 4 - Obstacle avoidance
        obstacle_avoidance_strength = 0.02  #0.015
        obstacle_avoidance = np.zeros(3)
        num_close_obstacles = 0

        # Loop through the obstacles and add avoidance if the drone is too close (subtract the object radius)
        for i in range(self.num_cylinders):
            diff = cylinder_poses[i] - drone_pos
            dist = np.linalg.norm(diff)

            # Account for the radius of the obstacle
            # diff = diff / dist * (dist - self.cylinder_radius)

            if dist < 1:
                obstacle_avoidance -= diff / (dist**2)
                num_close_obstacles += 1

        # Multiply by coefficient and divide by number of close obstacles
        if num_close_obstacles > 0:
            obstacle_avoidance *= obstacle_avoidance_strength/num_close_obstacles

        velocity_command = cohesion + separation + alignment + obstacle_avoidance
        
        return velocity_command
    
    # Compute the velocity command to send to each drone using the Olfati-Saber flocking algorithm
    def olfati_saber_input(self, drone_id, drone_pose, neighbour_poses, cylinder_poses):

        drone_pos = drone_pose[0]
        drone_vel = drone_pose[1]
        
        neighbour_pos = [neighbour_pose[0] for neighbour_pose in neighbour_poses]
        num_neighbours = len(neighbour_pos)
        
        # Define constants
        d_ref = self.target_separation
        d_ref_obs = 1.0

        r0_coh = 10              # Could use this perception radius to limit the number of neighbours
        delta = 0.1

        a = 0.3                 # 0.3
        b = 0.5                 # 0.5
        c = (b - a)/(2*np.sqrt(a*b))

        c_vm = 1                    # Coefficient of velocity matching

        r0_obs = 0.6    #0.6              # Radius of obstacle avoidance
        lambda_obs = 1              # (0,1]
        c_pm_obs = 4.3    #4.5            # Coefficient of obstacle avoidance
        c_vm_obs = 0             # Coefficient of velocity matching

        # Get the refence velocity and direction from the cmd_vel topic and rotate to the global reference frame
        v_ref = [self.__cmd_vel.linear.x, self.__cmd_vel.linear.y, self.__cmd_vel.linear.z]   #self.__cmd_vel.linear.z] could change max vel here
        v_ref = self.rot_body2global(v_ref, drone_pose[2][2])

        if np.linalg.norm(v_ref) > 0:
            v_ref_u = v_ref / np.linalg.norm(v_ref)
        else:
            v_ref_u = v_ref

        # Compute the velocity matching force
        acc_vel = c_vm * (v_ref - drone_vel)

        # Initialize the cohesion command
        acc_coh = np.zeros(3)

        # Go through all the neighbours of the drone and compute the Olfati-Saber swarm commands
        if num_neighbours > 0:

            # Loop through the neighbours
            for i in range(num_neighbours):
                pos_rel = neighbour_pos[i] - drone_pos
                # pos_rel[2] = 0
                dist = np.linalg.norm(pos_rel)

                # Compute the cohesion force
                acc_coh += self.get_cohesion_force(dist, d_ref, a, b, c, r0_coh, delta)*pos_rel/dist

        # Initialize the obstacle avoidance commands
        acc_obs = np.zeros(3)

        # Compute the obstacle avoidance commands
        for i in range(self.num_cylinders):
            
            cylinder_poses[i][2] = drone_pos[2]

            pos_rel = drone_pos - cylinder_poses[i]
            dist = np.linalg.norm(pos_rel) - self.cylinder_radius

            if dist < r0_obs:

                # s in range (0,1]
                s = self.cylinder_radius / (dist + self.cylinder_radius)
                pos_obs = s*drone_pos + (1-s)*cylinder_poses[i]

                # Derivative of s
                s_der = self.cylinder_radius * (drone_vel * (pos_obs - drone_pos) / dist) / (self.cylinder_radius + dist)**2
                vel_obs = s * drone_vel - self.cylinder_radius * (s_der/s) * (pos_obs-drone_pos)/dist
                pos_gamma = cylinder_poses[i] + lambda_obs * v_ref_u
                d_ag = np.linalg.norm(pos_gamma - pos_obs)

                acc_obs += c_pm_obs * self.get_neighbour_weight(dist/r0_obs, r0_coh, delta) * (self.get_cohesion_force(dist, d_ref_obs, a, b, c, r0_coh, delta)*(pos_obs - drone_pos)/dist +
                                      self.get_cohesion_force(d_ag, d_ref_obs, a, b, c, r0_coh, delta)*(pos_gamma - drone_pos)/(np.linalg.norm(pos_gamma - drone_pos))) + c_vm_obs * (vel_obs - drone_vel)

        # Rotate the global commands to the drone reference frame
        acc_command = acc_coh + acc_obs

        # Add the velocity matching command
        acc_command += acc_vel

        # Integrate the acceleration to get the velocity command
        velocity_command = acc_command * self.dt

        return velocity_command
            
    # Compute the velocity command to send to each drone using the Vasarhelyi model
    def vasarhelyi_input(self, drone_id, drone_pose, neighbour_poses, cylinder_poses):

        # NOTES:
        # - Currently assume all drones in the swarm are neighbours
        # - Could update to a matrix update for efficiency
        
        # Get the position and velocity of each drone
        drone_pos = drone_pose[0]
        drone_vel = drone_pose[1]
        
        # Get the position of each neighbour
        neighbour_pos = [neighbour_pose[0] for neighbour_pose in neighbour_poses]
        neighbour_vel = [neighbour_pose[1] for neighbour_pose in neighbour_poses]
        num_neighbours = len(neighbour_pos)

        # Initialise the velocity commands
        vel_rep = np.zeros(3)
        vel_fric = np.zeros(3)
        vel_obs = np.zeros(3)

        # Repulsion constants
        r0_rep = self.target_separation #0.75        # Radius of repulsion
        p_rep = 0.05        # Repulsion gain

        # Friction constants
        r0_fric = 85.3      # Radius of friction
        c_fric = 0.05       # Coefficient of velocity alignment
        v_fric = 0.63       # Velocity slack of alignment
        p_fric = 3.2        # Gain of braking curve
        a_fric = 4.16       # Acceleration of braking curve

        # Obstacle constants
        r0_shill = 0.1      # Stopping point offset of walls
        v_shill = 0.1       # Velocity of virtual shill agents
        p_shill = 0.24       # Gain of braking curve for walls
        a_shill = 0.1      # Acceleration of braking curve for walls
        min_obs_dist = 0.1  # Minimum distance to obstacle wall

        # Self-propulsion constants
        v_ref = 0         # Reference velocity

        # Go through all the neighbours of the drone and compute the velocity commands
        if num_neighbours > 0:

            # Loop through the neighbours
            for i in range(num_neighbours):
                
                # Compute relative position, distance, and vector
                pos_rel = neighbour_pos[i] - drone_pos
                dist = np.linalg.norm(pos_rel)
                pos_rel_u = -pos_rel / dist

                # Compute relative velocity, magnitude, and vector
                vel_rel = drone_vel - neighbour_vel[i]
                vel_rel_norm = np.linalg.norm(vel_rel)
                if vel_rel_norm > 0:
                    vel_rel_u = -vel_rel / vel_rel_norm

                # Compute the repulsion velocity
                if dist < r0_rep:
                    vel_rep += p_rep * (r0_rep - dist) * pos_rel_u
                else:
                    vel_rep += p_rep * (dist - r0_rep) * -pos_rel_u


                # Compute the friction velocity
                v_fric_max = self.get_v_max(v_fric, (dist - r0_fric), a_fric, p_fric)
                if vel_rel_norm > v_fric_max:
                    vel_fric += c_fric * (vel_rel_norm - v_fric_max) * vel_rel_u


        # # Go through all the obstacles and compute the velocity commands
        # for i in range(self.num_cylinders):

        #     # Compute relative xy position, distance, and vector
        #     pos_rel =  drone_pos[:2] - cylinder_poses[i][:2]
        #     dist = np.linalg.norm(pos_rel) - self.cylinder_radius


        #     # Compute the virtual agent velocity
        #     v_virtual = pos_rel / (dist + self.cylinder_radius) * v_shill

        #     # Compute the relative velocity
        #     v_virtual_rel = v_virtual - drone_vel[:2]
        #     v_virtual_rel_mag = np.linalg.norm(v_virtual_rel)

        #     # Check minimum distance - this is included but reversed in enrica's code, assumed typo
        #     # if dist < min_obs_dist:
        #     #     dist = min_obs_dist

        #     # Compute the obstacle velocity
        #     v_shill_max = self.get_v_max(0, (dist - r0_shill), a_shill, p_shill)

        #     if v_virtual_rel_mag > v_shill_max:
        #         # Log the sizes of vel_obs, v_virtual_rel and v_virtual_rel_mag
        #         # self.__node.get_logger().info("vel_obs: {}, v_virtual_rel: {}, v_virtual_rel_mag: {}".format(vel_obs, v_virtual_rel, v_virtual_rel_mag))
                # vel_obs[:2] += (v_virtual_rel_mag - v_shill_max) * v_virtual_rel / v_virtual_rel_mag


        #--------------------------------------REYNOLDS--------------------------------------
        # Rule 4 - Obstacle avoidance
        obstacle_avoidance_strength = 0.02
        obstacle_avoidance = np.zeros(3)
        num_close_obstacles = 0

        # Loop through the obstacles and add avoidance if the drone is too close (subtract the object radius)
        for i in range(self.num_cylinders):
            diff = cylinder_poses[i] - drone_pos
            dist = np.linalg.norm(diff)

            # Account for the radius of the obstacle
            # diff = diff / mag_diff * (mag_diff - self.cylinder_radius)

            if dist < 1:
                obstacle_avoidance -= diff / (dist**2)
                num_close_obstacles += 1

        # Multiply by coefficient and divide by number of close obstacles
        if num_close_obstacles > 0:
            obstacle_avoidance *= obstacle_avoidance_strength/num_close_obstacles
        vel_obs = obstacle_avoidance
        #------------------------------------------------------------------------------------
        
        # Compute velocity vector for self-propulsion
        drone_vel_u = drone_vel / np.linalg.norm(drone_vel)
        
        # Compute and return the velocity command with self-propulsion
        velocity_command = vel_rep + vel_fric + vel_obs + v_ref * drone_vel_u

        return velocity_command

    # Compute the angular velocity command to send to each drone using the swarm average
    def angular_input(self, drone_id, drone_pose, neighbour_poses):

        # Send the drone towards the average yaw of it's neighbours
        yaw_rate_coeff = 0.4

        drone_yaw = drone_pose[2][2]
        neighbour_yaws = [neighbour_pose[2][2] for neighbour_pose in neighbour_poses]
        num_neighbours = len(neighbour_yaws)

        # Initialize the angular velocity command
        angular_command = np.zeros(3)

        # Go through all the neighbours of the drone and compute the average yaw
        average_yaw_diff = 0
        if num_neighbours > 0:
                
                # Loop through the neighbours
                for i in range(num_neighbours):
                    yaw_diff = neighbour_yaws[i] - drone_yaw
                    if yaw_diff > np.pi:
                        yaw_diff -= 2*np.pi
                    elif yaw_diff < -np.pi:
                        yaw_diff += 2*np.pi
                    average_yaw_diff += yaw_diff
    
                # Divide by number of neighbours
                average_yaw_diff /= num_neighbours
        
            # # Add the yaw_diff
            # average_yaw_diff += yaw_diff_pos*0.2
        
        # Compute the yaw_command
        angular_command[2] = average_yaw_diff * yaw_rate_coeff
    
        # Return the angular velocity command
        return angular_command
    
    def step(self):

        rclpy.spin_once(self.__node, timeout_sec=0)

        # Increment the time
        self.time = self.__robot.getTime()
        
        # Get the position and velocity of each drone
        drone_poses = self.get_drone_poses()

        # Print the position of the cylinder objects on the first step using ROS logger
        cylinder_poses = self.get_cylinder_positions()
        if self.num_cylinders > 0:
            cylinder_poses[:,2] = np.mean(drone_poses[:,0,2])

        
        # Get the viewpoint position and orientation
        viewpoint_orientation = self.__robot.getFromDef('VIEWPOINT').getField('orientation')
        viewpoint_position = self.__robot.getFromDef('VIEWPOINT').getField('position')
            
        # Average the position of the swarm
        avg_pos = self.get_average_drone_pos(drone_poses, self.swarm_prev_pos)

        if avg_pos[2] > 0.45 and not self.takeoff:
            self.takeoff = True
        

        # Calcualte the average seperation distance between drones
        avg_sep = self.get_average_seperation(drone_poses)

        # Calcualte the average yaw of the swarm
        avg_yaw = self.get_average_yaw(drone_poses)

        viewpoint_orientation.setSFRotation([0,0,1,avg_yaw])
        viewpoint_position_body = self.rot_body2global(self.viewpoint_default_position, avg_yaw)
        viewpoint_position_body = (np.array(viewpoint_position_body) + avg_pos).tolist()
        viewpoint_position.setSFVec3f(viewpoint_position_body)

        # Write the time, average positions, velocities, and seperation to the log file
        self.log_file.write("{}, {}, {}, {}, ".format(self.time, np.mean(drone_poses[:,0,0]), np.mean(drone_poses[:,0,1]), np.mean(drone_poses[:,0,2])))
        self.log_file.write("{}, {}, {}, {}\n".format(np.mean(drone_poses[:,1,0]), np.mean(drone_poses[:,1,1]), np.mean(drone_poses[:,1,2]), avg_sep))

        # Compute the velocity command to send to each drone, assume all other drones are neighbours
        for i in range(self.num_drones):
            
            # Select the current drone
            drone_pose = drone_poses[i]
            
            # Check if the drone is upside down
            if np.abs(drone_pose[2][0]) > np.pi/3 or np.abs(drone_pose[2][1]) > np.pi/3:
                self.drone_in_swarm[i] = False
                continue

            # Check if the drone is spinning out of control
            if not self.check_safe_spin(drone_pose):
                self.drone_in_swarm[i] = False
                continue

            # Remove if the drone is too far away from the swarm
            if np.linalg.norm(drone_pose[0] - avg_pos) > 3 * self.target_separation:
                self.drone_in_swarm[i] = False
                continue

            # Set the drone as still in the swarm
            self.drone_in_swarm[i] = True

            # TODO: Use the boolean array for the neighbours instead of repeating the checks

            # Remove the current drone and drones that are upside down from the neighbour list
            neighbour_poses = np.delete(drone_poses, i, axis=0)
            neighbour_poses = neighbour_poses[np.where(np.abs(neighbour_poses[:,2,0]) < np.pi/3)]
            neighbour_poses = neighbour_poses[np.where(np.abs(neighbour_poses[:,2,1]) < np.pi/3)]

            # Remove neighbours that are spinning out of control
            neighbour_poses = np.array([pose for pose in neighbour_poses if self.check_safe_spin(pose)])

            # Remove neighbours that are too far away from the drone
            neighbour_poses = np.array([pose for pose in neighbour_poses if np.linalg.norm(pose[0] - drone_pose[0]) < 3 * self.target_separation])

            # velocity_swarm_command = self.reynolds_input(i, drone_pose, neighbour_poses, cylinder_poses)
            velocity_swarm_command = self.olfati_saber_input(i, drone_pose, neighbour_poses, cylinder_poses)
            # velocity_swarm_command = self.vasarhelyi_input(i, drone_pose, neighbour_poses, cylinder_poses)

            angular_command = self.angular_input(i, drone_pose, neighbour_poses)

            # Send the velocity command to each drone through ROS
            msg = Twist()
            msg.linear.x = velocity_swarm_command[0]
            msg.linear.y = velocity_swarm_command[1]
            msg.linear.z = velocity_swarm_command[2]
            msg.angular.x = angular_command[0]
            msg.angular.y = angular_command[1]
            msg.angular.z = angular_command[2]
            self.__drone_publishers[i].publish(msg)

        
        # Construct and publish the swarm state
        swarm_msg = SwarmState()

        swarm_msg.header.stamp = Time(seconds=self.time).to_msg()

        swarm_msg.swarm_pose.position.x = avg_pos[0]       # TODO Remove 'swarm_' from these titles
        swarm_msg.swarm_pose.position.y = avg_pos[1]
        swarm_msg.swarm_pose.position.z = avg_pos[2]
        swarm_msg.swarm_pose.orientation.x = 0.0
        swarm_msg.swarm_pose.orientation.y = 0.0
        swarm_msg.swarm_pose.orientation.z = avg_yaw
        swarm_msg.swarm_pose.orientation.w = 0.0

        swarm_msg.swarm_rates.linear.x = np.mean(drone_poses[:,1,0])
        swarm_msg.swarm_rates.linear.y = np.mean(drone_poses[:,1,1])
        swarm_msg.swarm_rates.linear.z = avg_sep #np.mean(drone_poses[:,1,2])
        swarm_msg.swarm_rates.angular.x = 0.0
        swarm_msg.swarm_rates.angular.y = 0.0
        swarm_msg.swarm_rates.angular.z = self.target_separation

        swarm_msg.swarm_inputs.linear.x = self.__cmd_vel.linear.x
        swarm_msg.swarm_inputs.linear.y = self.__cmd_vel.linear.y
        swarm_msg.swarm_inputs.linear.z = self.__cmd_vel.linear.z
        swarm_msg.swarm_inputs.angular.x = self.__cmd_vel.angular.x
        swarm_msg.swarm_inputs.angular.y = self.__cmd_vel.angular.y
        swarm_msg.swarm_inputs.angular.z = self.__cmd_vel.angular.z
        
        swarm_msg.drone_status = self.drone_in_swarm

        self.__swarm_publisher.publish(swarm_msg)
            

        self.swarm_prev_pos = avg_pos
        self.count += 1


